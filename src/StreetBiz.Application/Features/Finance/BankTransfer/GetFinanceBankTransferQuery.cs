using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Payments;

namespace StreetBiz.Application.Features.Finance.BankTransfer;

/// <summary>
/// FEE-01: the VietQR of a fee payment still waiting for its payOS bank transfer, so the
/// payment page can show it again after a reload. 404 once it is settled.
/// </summary>
public sealed record GetFinanceBankTransferQuery(long TransactionId) : IRequest<BankTransferDto>;

public sealed class GetFinanceBankTransferQueryValidator : AbstractValidator<GetFinanceBankTransferQuery>
{
    public GetFinanceBankTransferQueryValidator() => RuleFor(x => x.TransactionId).GreaterThan(0);
}

public sealed class GetFinanceBankTransferQueryHandler(
    IVendorContext vendorContext,
    IFinanceRepository finance,
    IPaymentGateway paymentGateway,
    IPaymentTransactionRepository? transactions = null) : IRequestHandler<GetFinanceBankTransferQuery, BankTransferDto>
{
    public async Task<BankTransferDto> Handle(GetFinanceBankTransferQuery request, CancellationToken cancellationToken)
    {
        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);
        var payment = await finance.GetVendorPaymentAsync(vendorId, request.TransactionId, cancellationToken)
            ?? throw new NotFoundException(FinanceMessages.TransactionNotFound);
        BankTransferInstructions? instructions = null;
        if (payment.Status == PaymentStatuses.Pending
            && payment.Provider == PaymentProviders.PayOs
            && transactions is not null)
        {
            // The payOS link opened at checkout, shown again while it is still payable.
            var stored = await transactions.GetAsync(payment.TransactionId, cancellationToken);
            instructions = stored is null
                ? null
                : paymentGateway.DescribeStoredCheckout(PaymentProviders.PayOs, stored.CheckoutPayload, stored.Amount);
        }
        return BankTransferDto.From(instructions)
            ?? throw new NotFoundException("Khoản thanh toán này không chờ chuyển khoản.");
    }
}
