using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Features.Finance.FeeReminders;

namespace StreetBiz.Infrastructure.Services;

public sealed class FeeReminderOptions
{
    public const string SectionName = "FeeReminders";

    /// <summary>How often the sweep runs. The sweep itself is idempotent per calendar day, so a
    /// shorter interval only makes a reminder arrive sooner within the same day, never twice.</summary>
    public int SweepIntervalMinutes { get; set; } = 60;
}

/// <summary>
/// FEE-02/SYS-06 background timer. Each tick runs three independent steps, in this order:
/// reconcile schedules with their contracts (SYS-03: bill contracts nothing bills, close the
/// instalments of contracts that ended early), the overdue/reminder sweep, then settle or close
/// payments left PENDING. Reconciling first means no reminder goes out for an instalment that is
/// about to be closed.
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
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, options.Value.SweepIntervalMinutes));
        using var timer = new PeriodicTimer(interval, clock);

        // Run once at startup, then on the timer, so a fresh deploy does not wait a full interval
        // before the first sweep.
        await RunOnceAsync(stoppingToken);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunOnceAsync(stoppingToken);
        }
    }

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

        await StepAsync("Stale payment sweep", async sender =>
        {
            var result = await sender.Send(new SettleStalePaymentsCommand(), cancellationToken);
            if (result.SettledCount > 0 || result.ClosedCount > 0)
            {
                logger.LogInformation(
                    "Stale payments: {Settled} settled with their provider, {Closed} closed.",
                    result.SettledCount, result.ClosedCount);
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
