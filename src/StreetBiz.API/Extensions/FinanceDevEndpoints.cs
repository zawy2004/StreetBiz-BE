using MediatR;
using StreetBiz.Application.Common.Events;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Features.Finance.FeeReminders;

namespace StreetBiz.API.Extensions;

/// <summary>
/// Development-only shortcuts for the finance module: raise the SYS-03 events by hand (the ward's
/// approval and renewal workflows do not publish them yet) and run the background sweeps now
/// instead of on their timer. They are never mapped outside Development.
/// </summary>
public static class FinanceDevEndpoints
{
    public static void MapFinanceDevApi(this WebApplication app)
    {
        if (!(app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing")))
        {
            return;
        }

        // Stands in for WARD-08/WARD-09 publishing the event on approval.
        app.MapPost("/api/dev/contracts/{contractId:long}/fee-schedule", async (
            long contractId,
            long actorUserId,
            IPublisher publisher,
            IFinanceRepository finance,
            CancellationToken cancellationToken) =>
        {
            await publisher.Publish(
                new RentalContractApprovedEvent(contractId, actorUserId, "Development shortcut"),
                cancellationToken);

            var schedule = await finance.GetCurrentFeeScheduleAsync(contractId, cancellationToken);
            return schedule is null ? Results.NotFound() : Results.Ok(schedule);
        })
        .AllowAnonymous()
        .WithTags("Development");

        app.MapGet("/api/dev/contracts/{contractId:long}/fee-schedule", async (
            long contractId,
            IFinanceRepository finance,
            CancellationToken cancellationToken) =>
        {
            var schedule = await finance.GetCurrentFeeScheduleAsync(contractId, cancellationToken);
            return schedule is null ? Results.NotFound() : Results.Ok(schedule);
        })
        .AllowAnonymous()
        .WithTags("Development");

        // FeeReminderHostedService already runs this on its own timer; this lets a manual test
        // trigger a sweep immediately instead of waiting for the next tick, and optionally replay
        // a specific day.
        app.MapPost("/api/dev/finance/reminders/sweep", async (
            DateOnly? today,
            ISender sender,
            TimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            var day = today ?? BusinessCalendar.Today(clock);
            var result = await sender.Send(new RunFeeReminderSweepCommand(day), cancellationToken);
            return Results.Ok(result);
        })
        .AllowAnonymous()
        .WithTags("Development");

        // Stands in for WARD-09 publishing RentalContractRenewedEvent: bills the days after
        // previousEnd up to newEnd on the contract's open schedule.
        app.MapPost("/api/dev/contracts/{contractId:long}/fee-schedule/extend", async (
            long contractId,
            DateOnly previousEnd,
            DateOnly newEnd,
            long actorUserId,
            IPublisher publisher,
            IFinanceRepository finance,
            CancellationToken cancellationToken) =>
        {
            await publisher.Publish(
                new RentalContractRenewedEvent(contractId, previousEnd, newEnd, actorUserId), cancellationToken);
            return Results.Ok(await finance.GetCurrentFeeScheduleAsync(contractId, cancellationToken));
        })
        .AllowAnonymous()
        .WithTags("Development");

        // The two other steps of FeeReminderHostedService, on demand.
        app.MapPost("/api/dev/finance/schedules/reconcile", async (
            ISender sender,
            TimeProvider clock,
            CancellationToken cancellationToken) =>
            Results.Ok(await sender.Send(new ReconcileFeeSchedulesCommand(BusinessCalendar.Today(clock)), cancellationToken)))
        .AllowAnonymous()
        .WithTags("Development");

        app.MapPost("/api/dev/finance/payments/settle-stale", async (
            ISender sender,
            CancellationToken cancellationToken) =>
            Results.Ok(await sender.Send(new SettleStalePaymentsCommand(), cancellationToken)))
        .AllowAnonymous()
        .WithTags("Development");
    }
}
