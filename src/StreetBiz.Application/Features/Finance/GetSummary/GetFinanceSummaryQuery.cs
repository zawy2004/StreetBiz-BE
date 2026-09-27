using MediatR;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Finance;

namespace StreetBiz.Application.Features.Finance.GetSummary;

public sealed record GetFinanceSummaryQuery : IRequest<FinanceSummaryDto>;

public sealed class GetFinanceSummaryQueryHandler(IVendorContext vendorContext, IFinanceRepository finance)
    : IRequestHandler<GetFinanceSummaryQuery, FinanceSummaryDto>
{
    public async Task<FinanceSummaryDto> Handle(GetFinanceSummaryQuery request, CancellationToken cancellationToken)
    {
        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);
        var summary = await finance.GetSummaryAsync(vendorId, cancellationToken);
        return summary.ToDto();
    }
}
