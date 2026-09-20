using MediatR;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.AddressChanges;

namespace StreetBiz.Application.Features.AddressChanges.TrackAddressChanges;

/// <summary>SIDE-09: track the caller's own address change requests, across all registrations.</summary>
public sealed record TrackAddressChangesQuery : IRequest<IReadOnlyList<AddressChangeRequestDto>>;

public sealed class TrackAddressChangesQueryHandler(
    IVendorContext vendorContext,
    IAddressChangeRequestRepository addressChanges)
    : IRequestHandler<TrackAddressChangesQuery, IReadOnlyList<AddressChangeRequestDto>>
{
    public async Task<IReadOnlyList<AddressChangeRequestDto>> Handle(TrackAddressChangesQuery request, CancellationToken cancellationToken)
    {
        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);
        var items = await addressChanges.ListByVendorAsync(vendorId, cancellationToken);
        return items.Select(r => r.ToDto()).ToList();
    }
}
