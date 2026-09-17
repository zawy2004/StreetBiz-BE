using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.SlotTransfers;

namespace StreetBiz.Application.Features.SlotTransfers.ListTransfers;

/// <summary>SIDE-12/13: list the caller's own slot transfer requests, sent (outgoing) or received (incoming).</summary>
public sealed record ListTransfersQuery(string Direction) : IRequest<IReadOnlyList<SlotTransferRequestDto>>;

public sealed class ListTransfersQueryValidator : AbstractValidator<ListTransfersQuery>
{
    public ListTransfersQueryValidator()
    {
        RuleFor(x => x.Direction).Must(d => TransferDirections.All.Contains(d))
            .WithMessage($"Direction must be one of: {string.Join(", ", TransferDirections.All)}.");
    }
}

public sealed class ListTransfersQueryHandler(
    IVendorContext vendorContext,
    ISlotTransferRequestRepository transfers)
    : IRequestHandler<ListTransfersQuery, IReadOnlyList<SlotTransferRequestDto>>
{
    public async Task<IReadOnlyList<SlotTransferRequestDto>> Handle(ListTransfersQuery request, CancellationToken cancellationToken)
    {
        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);
        var items = await transfers.ListByVendorAsync(vendorId, request.Direction, cancellationToken);
        return items.Select(r => r.ToDto()).ToList();
    }
}
