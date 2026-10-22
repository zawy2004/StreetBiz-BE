using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.Application.Features.AddressChanges.ReleaseAdjacentSlots;

/// <summary>
/// SIDE-10 / BR-22, run on a schedule (see FeeReminderHostedService): gives back the previous
/// adjacent slot of every open address change once its grace period is over and nothing is owed on
/// it. While something is owed the slot stays with the vendor and they are told, at most once a day.
/// Idempotent: a released contract is no longer ACTIVE, so the next run does not see it.
/// </summary>
public sealed record ReleaseDueAdjacentSlotsCommand(DateOnly Today) : IRequest<AdjacentSlotReleaseSweepResult>;

public sealed class ReleaseDueAdjacentSlotsCommandHandler(
    IAddressChangeRequestRepository addressChanges,
    IRentalContractRepository contracts,
    IRenewalRequestRepository renewals)
    : IRequestHandler<ReleaseDueAdjacentSlotsCommand, AdjacentSlotReleaseSweepResult>
{
    public async Task<AdjacentSlotReleaseSweepResult> Handle(
        ReleaseDueAdjacentSlotsCommand request,
        CancellationToken cancellationToken)
    {
        var released = 0;
        var waiting = 0;
        foreach (var pending in await addressChanges.ListPendingReleasesAsync(cancellationToken))
        {
            if (!AdjacentSlotRelease.IsDue(pending.GraceEndsOn, request.Today))
            {
                continue;
            }

            if (pending.HasDebt)
            {
                await addressChanges.NotifyReleaseBlockedAsync(pending, request.Today, cancellationToken);
                waiting++;
                continue;
            }

            try
            {
                // The same cancellation as SIDE-07 (the vendor returning a slot): future instalments
                // are dropped, the slot becomes available, and the DB trigger refuses it if a debt
                // appeared since we looked.
                await contracts.CancelAsync(
                    pending.ContractId,
                    pending.VendorUserId,
                    $"Trả ô liền kề cũ sau thời gian ân hạn của yêu cầu đổi địa chỉ #{pending.AddressChangeId}.",
                    cancellationToken);
                await renewals.CloseOpenForContractAsync(pending.ContractId, cancellationToken);
                await addressChanges.RecordReleaseAsync(pending, cancellationToken);
                released++;
            }
            catch (AppException)
            {
                waiting++;
            }
        }

        return new AdjacentSlotReleaseSweepResult(released, waiting);
    }
}
