using MediatR;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.VendorRegistration;

namespace StreetBiz.Application.Features.VendorRegistration.TrackRegistrations;

/// <summary>REG-03: list the caller's own registrations and their review status.</summary>
public sealed record TrackRegistrationsQuery : IRequest<IReadOnlyList<BusinessRegistrationDto>>;

public sealed class TrackRegistrationsQueryHandler(
    IVendorContext vendorContext,
    IBusinessRegistrationRepository repository)
    : IRequestHandler<TrackRegistrationsQuery, IReadOnlyList<BusinessRegistrationDto>>
{
    public async Task<IReadOnlyList<BusinessRegistrationDto>> Handle(TrackRegistrationsQuery request, CancellationToken cancellationToken)
    {
        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);
        var items = await repository.ListByVendorAsync(vendorId, cancellationToken);
        return items.Select(r => r.ToDto()).ToList();
    }
}
