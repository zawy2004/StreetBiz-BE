using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.Application.Features.Finance.InvoiceDocuments;

/// <summary>FEE-03 "tải hoá đơn": the caller's own payment receipt as a PDF.</summary>
public sealed record GetInvoicePdfQuery(long InvoiceId) : IRequest<FinanceFile>;

public sealed class GetInvoicePdfQueryValidator : AbstractValidator<GetInvoicePdfQuery>
{
    public GetInvoicePdfQueryValidator() => RuleFor(x => x.InvoiceId).GreaterThan(0);
}

public sealed class GetInvoicePdfQueryHandler(
    IVendorContext vendorContext,
    IFinanceRepository finance,
    IFinanceDocumentRenderer renderer,
    TimeProvider clock) : IRequestHandler<GetInvoicePdfQuery, FinanceFile>
{
    public async Task<FinanceFile> Handle(GetInvoicePdfQuery request, CancellationToken cancellationToken)
    {
        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);
        var invoice = await finance.GetInvoiceDocumentAsync(vendorId, request.InvoiceId, cancellationToken)
            ?? throw new NotFoundException(FinanceMessages.InvoiceNotFound);

        var pdf = renderer.RenderInvoicePdf(invoice, clock.GetUtcNow().UtcDateTime);
        return new FinanceFile($"{invoice.InvoiceNumber}.pdf", "application/pdf", pdf);
    }
}
