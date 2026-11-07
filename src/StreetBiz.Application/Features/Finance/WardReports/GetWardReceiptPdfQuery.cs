using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.WardSlots;

namespace StreetBiz.Application.Features.Finance.WardReports;

/// <summary>
/// The PDF of one receipt of the officer's ward: the same document the household downloads. A
/// receipt of another ward is not found, not forbidden, like every other ward-scoped read.
/// </summary>
public sealed record GetWardReceiptPdfQuery(long InvoiceId) : IRequest<FinanceFile>;

public sealed class GetWardReceiptPdfQueryValidator : AbstractValidator<GetWardReceiptPdfQuery>
{
    public GetWardReceiptPdfQueryValidator() => RuleFor(x => x.InvoiceId).GreaterThan(0);
}

public sealed class GetWardReceiptPdfQueryHandler(
    IWardActorContext wardActorContext,
    IWardReportRepository reports,
    IFinanceRepository finance,
    IFinanceDocumentRenderer renderer,
    TimeProvider clock) : IRequestHandler<GetWardReceiptPdfQuery, FinanceFile>
{
    public async Task<FinanceFile> Handle(GetWardReceiptPdfQuery request, CancellationToken cancellationToken)
    {
        var actor = await wardActorContext.RequireAsync(cancellationToken);
        var vendorId = await reports.GetReceiptVendorAsync(actor.WardId, request.InvoiceId, cancellationToken)
            ?? throw new NotFoundException(FinanceMessages.InvoiceNotFound);
        var invoice = await finance.GetInvoiceDocumentAsync(vendorId, request.InvoiceId, cancellationToken)
            ?? throw new NotFoundException(FinanceMessages.InvoiceNotFound);

        var pdf = renderer.RenderInvoicePdf(invoice, clock.GetUtcNow().UtcDateTime);
        return new FinanceFile($"{invoice.InvoiceNumber}.pdf", "application/pdf", pdf);
    }
}
