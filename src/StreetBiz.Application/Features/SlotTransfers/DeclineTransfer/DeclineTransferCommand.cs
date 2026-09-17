using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.Application.Features.SlotTransfers.DeclineTransfer;

/// <summary>SIDE-13: the receiving vendor declines a pending transfer.</summary>
public sealed record DeclineTransferCommand(long TransferId) : IRequest<Unit>;

public sealed class DeclineTransferCommandHandler(
    IVendorContext vendorContext,
    ISlotTransferRequestRepository transfers)
    : IRequestHandler<DeclineTransferCommand, Unit>
{
    public async Task<Unit> Handle(DeclineTransferCommand request, CancellationToken cancellationToken)
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

        await transfers.SetStatusAsync(request.TransferId, TransferStatuses.Rejected, null, cancellationToken);
        return Unit.Value;
    }
}
