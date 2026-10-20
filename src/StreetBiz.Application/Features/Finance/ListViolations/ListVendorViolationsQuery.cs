using MediatR;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Finance;

namespace StreetBiz.Application.Features.Finance.ListViolations;

/// <summary>FEE-05.</summary>
public sealed record ListVendorViolationsQuery : IRequest<IReadOnlyList<VendorViolationDto>>;

public sealed class ListVendorViolationsQueryHandler(IVendorContext vendorContext, IFinanceRepository finance)
    : IRequestHandler<ListVendorViolationsQuery, IReadOnlyList<VendorViolationDto>>
{
    public async Task<IReadOnlyList<VendorViolationDto>> Handle(
        ListVendorViolationsQuery request, CancellationToken cancellationToken)
    {
        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);
        var violations = await finance.ListVendorViolationsAsync(vendorId, cancellationToken);
        return violations.Select(violation => violation.ToDto()).ToList();
    }
}
