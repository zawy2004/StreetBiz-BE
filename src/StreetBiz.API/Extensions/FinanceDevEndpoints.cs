using MediatR;
using StreetBiz.Application.Common.Events;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Features.Finance.FeeReminders;

namespace StreetBiz.API.Extensions;

/// <summary>
/// Development-only shortcuts for the finance module. WARD-08/09 approval now writes the fee
/// schedule itself (WardComplianceService.PlanTermAsync, same calculator and planner); these
/// remain for regenerating the schedule of a seeded contract through
/// <see cref="RentalContractApprovedEvent"/>. They are never mapped outside Development.
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
    }
}
