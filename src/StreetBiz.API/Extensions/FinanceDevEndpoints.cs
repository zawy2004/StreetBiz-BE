using MediatR;
using StreetBiz.Application.Common.Events;
using StreetBiz.Application.Common.Interfaces;

namespace StreetBiz.API.Extensions;

/// <summary>
/// Development-only shortcuts for the finance module. WARD-08 (approve a rental application,
/// which is what creates a contract and publishes
/// <see cref="RentalContractApprovedEvent"/>) does not exist yet, so without these there is no
/// way to exercise SYS-03 against a real database. They are never mapped outside Development.
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
    }
}
