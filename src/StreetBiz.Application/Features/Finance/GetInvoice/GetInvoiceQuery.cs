using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Finance;

namespace StreetBiz.Application.Features.Finance.GetInvoice;

/// <summary>FEE-03: one invoice's full detail.</summary>
public sealed record GetInvoiceQuery(long InvoiceId) : IRequest<InvoiceDetailDto>;

public sealed class GetInvoiceQueryValidator : AbstractValidator<GetInvoiceQuery>
{
    public GetInvoiceQueryValidator() => RuleFor(x => x.InvoiceId).GreaterThan(0);
}

public sealed class GetInvoiceQueryHandler(IVendorContext vendorContext, IFinanceRepository finance)
    : IRequestHandler<GetInvoiceQuery, InvoiceDetailDto>
{
    public async Task<InvoiceDetailDto> Handle(GetInvoiceQuery request, CancellationToken cancellationToken)
    {
        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);
        var invoice = await finance.GetInvoiceAsync(vendorId, request.InvoiceId, cancellationToken)
            ?? throw new NotFoundException(FinanceMessages.InvoiceNotFound);
        // The receipt's own fields (the same read the PDF uses), so screen and PDF never differ.
        var document = await finance.GetInvoiceDocumentAsync(vendorId, request.InvoiceId, cancellationToken);
        return invoice.ToDetailDto() with
        {
            WardName = document?.WardName,
            PayerName = document?.PayerName,
            BusinessName = document?.BusinessName,
            ProviderReference = document?.ProviderReference,
            DecisionNumber = document?.DecisionNumber,
            AmountInWords = VietnameseMoneyWords.Of(invoice.Amount),
        };
    }
}
