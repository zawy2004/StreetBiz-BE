using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.DigitalPermits;

namespace StreetBiz.Application.Features.DigitalPermits.GetPermit;

/// <summary>
/// SIDE-08: view the digital QR permit for one of the caller's contracts. EffectiveStatus comes
/// from vw_PermitValidity, never from permit_status alone -- a vendor who returned their slot
/// must not still see a permit that reads ACTIVE.
/// </summary>
public sealed record GetPermitQuery(long ContractId) : IRequest<DigitalPermitDto>;

public sealed class GetPermitQueryHandler(
    IVendorContext vendorContext,
    IDigitalPermitRepository permits)
    : IRequestHandler<GetPermitQuery, DigitalPermitDto>
{
    public async Task<DigitalPermitDto> Handle(GetPermitQuery request, CancellationToken cancellationToken)
    {
        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);

        var permit = await permits.GetValidityByContractAsync(request.ContractId, cancellationToken)
            ?? throw new NotFoundException(SideMessages.PermitNotFound);

        if (permit.VendorId != vendorId)
        {
            throw new ForbiddenException(AppMessages.Forbidden);
        }

        return permit.ToDto();
    }
}
