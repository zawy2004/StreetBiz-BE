using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StreetBiz.Application.Features.Tax;

namespace StreetBiz.Infrastructure.Services;

public sealed class TaxSweepOptions
{
    public const string SectionName = "TaxSweep";

    /// <summary>
    /// How often the sweep runs. It is idempotent (a referral exists once, a notification is sent
    /// once per record), so a shorter interval only makes a new case or reminder appear sooner.
    /// </summary>
    public int IntervalMinutes { get; set; } = 360;
}

/// <summary>
/// Household tax background timer: refresh every ward's list of cases for the tax office and send
/// households their threshold and deadline reminders. Runs once at startup so a fresh deploy does
/// not wait a full interval, then on the timer. A hosted service is a singleton, so it resolves
/// <see cref="ISender"/> from a new scope per run (the repositories hold a scoped DbContext).
/// </summary>
public sealed class TaxSweepHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<TaxSweepOptions> options,
    TimeProvider clock,
    ILogger<TaxSweepHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, options.Value.IntervalMinutes));
        using var timer = new PeriodicTimer(interval, clock);

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
            var result = await scope.ServiceProvider.GetRequiredService<ISender>()
                .Send(new RunTaxSweepCommand(), cancellationToken);
            if (result.ReferralsCreated > 0 || result.ReferralsClosed > 0
                || result.ThresholdReminders > 0 || result.DeadlineReminders > 0)
            {
                logger.LogInformation(
                    "Tax sweep: {Created} referral(s) added, {Closed} closed by the system, "
                    + "{Threshold} threshold reminder(s) and {Deadline} deadline reminder(s) sent.",
                    result.ReferralsCreated, result.ReferralsClosed, result.ThresholdReminders, result.DeadlineReminders);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // A failed run must not crash the host: the next tick tries again, and the sweep is idempotent.
            logger.LogError(exception, "Tax sweep failed; will retry on the next tick.");
        }
    }
}
