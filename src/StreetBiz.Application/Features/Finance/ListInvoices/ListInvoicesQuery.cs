using MediatR;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Finance;

namespace StreetBiz.Application.Features.Finance.ListInvoices;

/// <summary>FEE-03: list the caller's invoices, most recent first.</summary>
public sealed record ListInvoicesQuery : IRequest<IReadOnlyList<InvoiceDto>>;

public sealed class ListInvoicesQueryHandler(IVendorContext vendorContext, IFinanceRepository finance)
    : IRequestHandler<ListInvoicesQuery, IReadOnlyList<InvoiceDto>>
{
    public async Task<IReadOnlyList<InvoiceDto>> Handle(ListInvoicesQuery request, CancellationToken cancellationToken)
    {
        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);
        var invoices = await finance.ListInvoicesAsync(vendorId, cancellationToken);
        return invoices.Select(invoice => invoice.ToListDto()).ToList();
    }
}
