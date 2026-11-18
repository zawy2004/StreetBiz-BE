using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Features.AddressChanges.ReleaseAdjacentSlots;
using StreetBiz.Application.Features.Finance.FeeReminders;

namespace StreetBiz.Infrastructure.Services;

public sealed class FeeReminderOptions
{
    public const string SectionName = "FeeReminders";

    /// <summary>How often the sweep runs. The sweep itself is idempotent per calendar day, so a
    /// shorter interval only makes a reminder arrive sooner within the same day, never twice.</summary>
    public int SweepIntervalMinutes { get; set; } = 60;

    /// <summary>
    /// How often payments left PENDING are settled or closed. Much shorter than the daily sweeps: a
    /// customer whose payOS/MoMo window has run out is waiting on it with a frozen cart, and each pass
    /// only asks the provider about payments that are already past their check delay.
    /// </summary>
    public int PaymentSweepIntervalMinutes { get; set; } = 5;
}

/// <summary>
/// FEE-02/SYS-06 background timers. The daily-style tick runs three independent steps, in this order:
/// reconcile schedules with their contracts (SYS-03: bill contracts nothing bills, close the
/// instalments of contracts that ended early), the overdue/reminder sweep, then the SIDE-10 release of
/// slots whose address-change grace period is over. Reconciling first means no reminder goes out for
/// an instalment that is about to be closed. A second, faster timer settles or closes payments left
/// PENDING (fees, penalties and online orders).
///
/// A hosted service is a singleton, so it resolves <see cref="ISender"/> from a new DI scope per
/// step rather than injecting it directly — the same reason ASP.NET Core scopes a request:
/// FinanceRepository holds a scoped DbContext.
/// </summary>
public sealed class FeeReminderHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<FeeReminderOptions> options,
    TimeProvider clock,
    ILogger<FeeReminderHostedService> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(
            RunEveryAsync(options.Value.SweepIntervalMinutes, RunOnceAsync, stoppingToken),
            RunEveryAsync(options.Value.PaymentSweepIntervalMinutes, RunPaymentSweepAsync, stoppingToken));

    private async Task RunEveryAsync(
        int intervalMinutes, Func<CancellationToken, Task> run, CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, intervalMinutes)), clock);

        // Run once at startup, then on the timer, so a fresh deploy does not wait a full interval
        // before the first sweep.
        await run(stoppingToken);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await run(stoppingToken);
        }
    }

    private Task RunPaymentSweepAsync(CancellationToken cancellationToken) =>
        StepAsync("Stale payment sweep", async sender =>
        {
            var result = await sender.Send(new SettleStalePaymentsCommand(), cancellationToken);
            if (result.SettledCount > 0 || result.ClosedCount > 0)
            {
                logger.LogInformation(
                    "Stale payments: {Settled} settled with their provider, {Closed} closed.",
                    result.SettledCount, result.ClosedCount);
            }
        }, cancellationToken);

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var today = BusinessCalendar.Today(clock);

        await StepAsync("Fee schedule reconciliation", async sender =>
        {
            var result = await sender.Send(new ReconcileFeeSchedulesCommand(today), cancellationToken);
            if (result.GeneratedCount > 0 || result.CancelledCount > 0 || result.FailedCount > 0)
            {
                logger.LogInformation(
                    "Fee schedules: {Generated} generated for unbilled contracts ({Failed} could not be), "
                    + "{Cancelled} instalment(s) closed after an early contract end.",
                    result.GeneratedCount, result.FailedCount, result.CancelledCount);
            }
        }, cancellationToken);

        await StepAsync("Fee reminder sweep", async sender =>
        {
            var result = await sender.Send(new RunFeeReminderSweepCommand(today), cancellationToken);
            if (result.OverdueCount > 0 || result.ReminderCount > 0)
            {
                logger.LogInformation(
                    "Fee reminder sweep: {OverdueCount} instalment(s) marked OVERDUE, {ReminderCount} reminder(s) sent.",
                    result.OverdueCount, result.ReminderCount);
            }
        }, cancellationToken);

        // After the reminder sweep, which is what turns a missed instalment into OVERDUE: a slot is
        // only held back by a debt the database already counts as one.
        await StepAsync("Adjacent slot release sweep", async sender =>
        {
            var result = await sender.Send(new ReleaseDueAdjacentSlotsCommand(today), cancellationToken);
            if (result.Released > 0 || result.WaitingForPayment > 0)
            {
                logger.LogInformation(
                    "Adjacent slot release: {Released} slot(s) returned after their grace period, "
                    + "{Waiting} held back by unpaid fees.",
                    result.Released, result.WaitingForPayment);
            }
        }, cancellationToken);
    }

    private async Task StepAsync(string name, Func<ISender, Task> step, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            await step(scope.ServiceProvider.GetRequiredService<ISender>());
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // A failed step must not crash the host or stop the other steps — the next tick tries
            // again, and every step is idempotent.
            logger.LogError(exception, "{Step} failed; will retry on the next tick.", name);
        }
    }
}
