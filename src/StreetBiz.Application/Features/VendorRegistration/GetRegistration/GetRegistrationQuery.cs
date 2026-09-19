using MediatR;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.VendorRegistration;

namespace StreetBiz.Application.Features.VendorRegistration.GetRegistration;

/// <summary>REG-03 detail: one of the caller's registrations with its evidence documents.</summary>
public sealed record GetRegistrationQuery(long RegistrationId) : IRequest<BusinessRegistrationDetailDto>;

public sealed class GetRegistrationQueryHandler(
    IVendorContext vendorContext,
    IBusinessRegistrationRepository repository)
    : IRequestHandler<GetRegistrationQuery, BusinessRegistrationDetailDto>
{
    public async Task<BusinessRegistrationDetailDto> Handle(GetRegistrationQuery request, CancellationToken cancellationToken)
    {
        var registration = await vendorContext.RequireOwnedRegistrationAsync(request.RegistrationId, cancellationToken);
        var evidence = await repository.ListEvidenceAsync(request.RegistrationId, cancellationToken);

        return new BusinessRegistrationDetailDto(
            registration.ToDto(), evidence.Select(e => e.ToDto()).ToList());
    }
}
