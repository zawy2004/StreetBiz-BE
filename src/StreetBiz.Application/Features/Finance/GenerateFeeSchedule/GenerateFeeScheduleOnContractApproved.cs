using MediatR;
using StreetBiz.Application.Common.Events;

namespace StreetBiz.Application.Features.Finance.GenerateFeeSchedule;

/// <summary>
/// Bridges WARD-08/WARD-09 to SYS-03: whoever approves a contract publishes
/// <see cref="RentalContractApprovedEvent"/> and this generates its fee schedule (BR-17/BR-18).
///
/// Failures are deliberately not swallowed. A contract with no fee schedule is an unbillable
/// contract, so the approval that created it should fail with it rather than leave the ward with
/// a tenancy nobody is invoicing. That puts one requirement on the publisher: publish inside the
/// same transaction as the approval write, so the contract and its schedule commit together.
/// Regenerating later is always safe — a new revision supersedes the current one.
/// </summary>
public sealed class GenerateFeeScheduleOnContractApproved(ISender sender)
    : INotificationHandler<RentalContractApprovedEvent>
{
    public Task Handle(RentalContractApprovedEvent notification, CancellationToken cancellationToken) =>
        sender.Send(
            new GenerateFeeScheduleCommand(notification.ContractId, notification.ActorUserId),
            cancellationToken);
}
