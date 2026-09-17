using MediatR;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.RentalApplications;

namespace StreetBiz.Application.Features.RentalApplications.TrackApplications;

/// <summary>SIDE-04: list the caller's own rental applications and their status.</summary>
public sealed record TrackApplicationsQuery : IRequest<IReadOnlyList<RentalApplicationDto>>;

public sealed class TrackApplicationsQueryHandler(
    IVendorContext vendorContext,
    IRentalApplicationRepository applications)
    : IRequestHandler<TrackApplicationsQuery, IReadOnlyList<RentalApplicationDto>>
{
    public async Task<IReadOnlyList<RentalApplicationDto>> Handle(TrackApplicationsQuery request, CancellationToken cancellationToken)
    {
        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);
        var items = await applications.ListByVendorAsync(vendorId, cancellationToken);
        return items.Select(a => a.ToDto()).ToList();
    }
}
