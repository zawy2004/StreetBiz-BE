using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.Application.Features.SlotTransfers.AcceptTransfer;

/// <summary>
/// SIDE-13: the receiving vendor accepts a pending transfer. Only marks ACCEPTED_BY_RECEIVER --
/// the ownership change itself is WARD-18's decision, not this handler's.
/// </summary>
public sealed record AcceptTransferCommand(long TransferId) : IRequest<Unit>;

public sealed class AcceptTransferCommandHandler(
    IVendorContext vendorContext,
    ISlotTransferRequestRepository transfers,
    IDateTimeProvider clock)
    : IRequestHandler<AcceptTransferCommand, Unit>
{
    public async Task<Unit> Handle(AcceptTransferCommand request, CancellationToken cancellationToken)
    {
        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);

        var transfer = await transfers.GetByIdAsync(request.TransferId, cancellationToken)
            ?? throw new NotFoundException(SideMessages.TransferNotFound);

        if (transfer.ToVendorId != vendorId)
        {
            throw new ForbiddenException(SideMessages.NotTheReceivingVendor);
        }

        if (transfer.TransferStatus != TransferStatuses.Pending)
        {
            throw new ConflictException(SideMessages.TransferNotPending);
        }

        await transfers.SetStatusAsync(request.TransferId, TransferStatuses.AcceptedByReceiver, clock.UtcNow, cancellationToken);
        return Unit.Value;
    }
}
