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
/// FEE-02/SYS-06 background timer. A hosted service is a singleton, so it resolves
/// <see cref="ISender"/> from a new DI scope per tick rather than injecting it directly — the
/// same reason ASP.NET Core scopes a request: FinanceRepository holds a scoped DbContext.
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
        try
        {
            using var scope = scopeFactory.CreateScope();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            var today = BusinessCalendar.Today(clock);
            var result = await sender.Send(new RunFeeReminderSweepCommand(today), cancellationToken);
            if (result.OverdueCount > 0 || result.ReminderCount > 0)
            {
                logger.LogInformation(
                    "Fee reminder sweep: {OverdueCount} instalment(s) marked OVERDUE, {ReminderCount} reminder(s) sent.",
                    result.OverdueCount, result.ReminderCount);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A failed sweep must not crash the host — the next tick tries again, and nothing here
            // is destructive (transitions and reminders are each individually idempotent per day).
            logger.LogError(exception, "Fee reminder sweep failed; will retry on the next tick.");
        }
    }
}
