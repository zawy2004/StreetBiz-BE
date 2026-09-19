using MediatR;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Sidewalk;

namespace StreetBiz.Application.Features.SlotProposals.TrackProposals;

/// <summary>SIDE-11: track the caller's own proposed slots and their review status.</summary>
public sealed record TrackProposalsQuery : IRequest<IReadOnlyList<SlotProposalDto>>;

public sealed class TrackProposalsQueryHandler(
    IVendorContext vendorContext,
    ISidewalkSlotRepository slots)
    : IRequestHandler<TrackProposalsQuery, IReadOnlyList<SlotProposalDto>>
{
    public async Task<IReadOnlyList<SlotProposalDto>> Handle(TrackProposalsQuery request, CancellationToken cancellationToken)
    {
        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);
        var items = await slots.ListProposalsByVendorAsync(vendorId, cancellationToken);
        return items.Select(s => s.ToDto()).ToList();
    }
}
