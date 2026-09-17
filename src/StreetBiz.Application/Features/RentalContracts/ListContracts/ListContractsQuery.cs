using MediatR;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.RentalContracts;

namespace StreetBiz.Application.Features.RentalContracts.ListContracts;

/// <summary>SIDE-05: list the caller's rental contracts, optionally filtered by status.</summary>
public sealed record ListContractsQuery(string? Status) : IRequest<IReadOnlyList<RentalContractDto>>;

public sealed class ListContractsQueryHandler(
    IVendorContext vendorContext,
    IRentalContractRepository contracts)
    : IRequestHandler<ListContractsQuery, IReadOnlyList<RentalContractDto>>
{
    public async Task<IReadOnlyList<RentalContractDto>> Handle(ListContractsQuery request, CancellationToken cancellationToken)
    {
        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);
        var items = await contracts.ListByVendorAsync(vendorId, request.Status, cancellationToken);
        return items.Select(c => c.ToDto()).ToList();
    }
}
