using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.RentalApplications;

namespace StreetBiz.Application.Features.RentalApplications.GetApplication;

/// <summary>SIDE-04: view one of the caller's rental applications.</summary>
public sealed record GetApplicationQuery(long ApplicationId) : IRequest<RentalApplicationDto>;

public sealed class GetApplicationQueryHandler(
    IVendorContext vendorContext,
    IRentalApplicationRepository applications)
    : IRequestHandler<GetApplicationQuery, RentalApplicationDto>
{
    public async Task<RentalApplicationDto> Handle(GetApplicationQuery request, CancellationToken cancellationToken)
    {
        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);

        var application = await applications.GetByIdAsync(request.ApplicationId, cancellationToken)
            ?? throw new NotFoundException(SideMessages.ApplicationNotFound);

        if (application.VendorId != vendorId)
        {
            throw new ForbiddenException(AppMessages.Forbidden);
        }

        return application.ToDto();
    }
}
