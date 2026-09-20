using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.RentalContracts;

namespace StreetBiz.Application.Features.RentalContracts.GetContract;

/// <summary>SIDE-05: view one of the caller's rental contracts.</summary>
public sealed record GetContractQuery(long ContractId) : IRequest<RentalContractDto>;

public sealed class GetContractQueryHandler(
    IVendorContext vendorContext,
    IRentalContractRepository contracts)
    : IRequestHandler<GetContractQuery, RentalContractDto>
{
    public async Task<RentalContractDto> Handle(GetContractQuery request, CancellationToken cancellationToken)
    {
        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);

        var contract = await contracts.GetByIdAsync(request.ContractId, cancellationToken)
            ?? throw new NotFoundException(SideMessages.ContractNotFound);

        if (contract.VendorId != vendorId)
        {
            throw new ForbiddenException(AppMessages.Forbidden);
        }

        return contract.ToDto();
    }
}
