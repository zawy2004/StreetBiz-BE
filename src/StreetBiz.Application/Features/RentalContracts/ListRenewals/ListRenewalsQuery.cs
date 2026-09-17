using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.RentalContracts;

namespace StreetBiz.Application.Features.RentalContracts.ListRenewals;

/// <summary>SIDE-06: track the renewal requests raised for one of the caller's contracts.</summary>
public sealed record ListRenewalsQuery(long ContractId) : IRequest<IReadOnlyList<RenewalRequestDto>>;

public sealed class ListRenewalsQueryHandler(
    IVendorContext vendorContext,
    IRentalContractRepository contracts,
    IRenewalRequestRepository renewals)
    : IRequestHandler<ListRenewalsQuery, IReadOnlyList<RenewalRequestDto>>
{
    public async Task<IReadOnlyList<RenewalRequestDto>> Handle(ListRenewalsQuery request, CancellationToken cancellationToken)
    {
        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);

        var contract = await contracts.GetByIdAsync(request.ContractId, cancellationToken)
            ?? throw new NotFoundException(SideMessages.ContractNotFound);

        if (contract.VendorId != vendorId)
        {
            throw new ForbiddenException(AppMessages.Forbidden);
        }

        var items = await renewals.ListByContractAsync(request.ContractId, cancellationToken);
        return items.Select(r => r.ToDto()).ToList();
    }
}
