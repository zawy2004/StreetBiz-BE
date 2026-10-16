using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Payments;

namespace StreetBiz.Application.Features.Finance.BankTransfer;

/// <summary>
/// FEE-01/FEE-04: the VietQR of a fee or penalty payment still waiting for its SePay bank
/// transfer, so the payment page can show it again after a reload. 404 once it is settled.
/// </summary>
public sealed record GetFinanceBankTransferQuery(long TransactionId) : IRequest<BankTransferDto>;

public sealed class GetFinanceBankTransferQueryValidator : AbstractValidator<GetFinanceBankTransferQuery>
{
    public GetFinanceBankTransferQueryValidator() => RuleFor(x => x.TransactionId).GreaterThan(0);
}

public sealed class GetFinanceBankTransferQueryHandler(
    IVendorContext vendorContext,
    IFinanceRepository finance,
    IPaymentGateway paymentGateway) : IRequestHandler<GetFinanceBankTransferQuery, BankTransferDto>
{
    public async Task<BankTransferDto> Handle(GetFinanceBankTransferQuery request, CancellationToken cancellationToken)
    {
        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);
        var payment = await finance.GetVendorPaymentAsync(vendorId, request.TransactionId, cancellationToken)
            ?? throw new NotFoundException(FinanceMessages.TransactionNotFound);
        var instructions = payment.Status == PaymentStatuses.Pending
            && payment.ProviderReference is { } reference
            && payment.Amount is { } amount
                ? paymentGateway.DescribeBankTransfer(payment.Provider, reference, amount)
                : null;
        return BankTransferDto.From(instructions)
            ?? throw new NotFoundException("Khoản thanh toán này không chờ chuyển khoản.");
    }
}
