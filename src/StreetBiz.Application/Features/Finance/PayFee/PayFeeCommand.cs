using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Finance;
using StreetBiz.Application.DTOs.Payments;
using StreetBiz.Application.Features.PayoutAccounts;

namespace StreetBiz.Application.Features.Finance.PayFee;

/// <summary>FEE-01: open a checkout for one fee instalment.</summary>
public sealed record PayFeeCommand(long FeeItemId, string Provider, string IdempotencyKey)
    : IRequest<FinanceCheckoutDto>;

public sealed class PayFeeCommandValidator : AbstractValidator<PayFeeCommand>
{
    public PayFeeCommandValidator()
    {
        RuleFor(x => x.FeeItemId).GreaterThan(0);
        RuleFor(x => x.Provider)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .Must(value => PaymentProviders.IsValid(value.Trim().ToUpperInvariant()))
            .WithMessage(PaymentProviders.InvalidMessage);
        RuleFor(x => x.IdempotencyKey).NotEmpty().MaximumLength(100);
    }
}

/// <summary>
/// FEE-01. PAYOS pays the ward that owns the slot straight into its own payOS account: the ward
/// must have connected an ACTIVE receiving account, and only that account's webhook settles it.
/// </summary>
public sealed class PayFeeCommandHandler(
    IVendorContext vendorContext,
    IFinanceRepository finance,
    IPaymentGateway paymentGateway,
    IPayeeAccountRepository? payees = null,
    PayeeCheckouts? payeeCheckouts = null) : IRequestHandler<PayFeeCommand, FinanceCheckoutDto>
{
    public async Task<FinanceCheckoutDto> Handle(PayFeeCommand request, CancellationToken cancellationToken)
    {
        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);
        var provider = request.Provider.Trim().ToUpperInvariant();
        var idempotencyKey = request.IdempotencyKey.Trim();

        var item = await finance.GetFeeItemForCheckoutAsync(vendorId, request.FeeItemId, cancellationToken)
            ?? throw new NotFoundException(FinanceMessages.FeeItemNotFound);
        if (!FeeItemStatuses.IsOutstanding(item.ItemStatus))
        {
            throw new DomainRuleException(FinanceMessages.FeeItemNotPayable);
        }
        if (!paymentGateway.IsProviderAvailable(provider))
        {
            throw new DomainRuleException(FinanceMessages.ProviderNotConfigured);
        }

        PayeeSummary? payee = null;
        if (provider == PaymentProviders.PayOs)
        {
            payee = payees is null ? null : await payees.GetForFeeItemAsync(request.FeeItemId, cancellationToken);
            if (payee is not { Active: true } || payeeCheckouts is null)
            {
                throw new DomainRuleException(FinanceMessages.WardNotAcceptingTransfers);
            }
        }

        var checkout = await finance.CreateFeeCheckoutAsync(
            vendorId, request.FeeItemId, provider, idempotencyKey, cancellationToken);
        var gatewayRequest = new PaymentGatewayCheckoutRequest(
            request.FeeItemId,
            item.FeeItemPeriodLabel(),
            checkout.TransactionId,
            checkout.IdempotencyKey,
            checkout.Provider,
            checkout.Amount,
            PaymentPurposes.RentalFee);

        PaymentGatewayCheckoutResult gatewayResult;
        try
        {
            gatewayResult = payee is not null && checkout.Provider == PaymentProviders.PayOs
                // Records the ward on the transaction, then opens (or shows again) its payOS link.
                ? await payeeCheckouts!.OpenAsync(payee, gatewayRequest, cancellationToken)
                : await paymentGateway.CreateCheckoutAsync(gatewayRequest, cancellationToken);
        }
        catch
        {
            // Otherwise the attempt sits in the vendor's payment history as PENDING forever.
            await finance.AbandonUnopenedCheckoutAsync(checkout.TransactionId, CancellationToken.None);
            throw;
        }

        if (payee is null && !string.IsNullOrWhiteSpace(gatewayResult.ProviderReference))
        {
            await finance.SetPaymentProviderReferenceAsync(
                checkout.TransactionId, gatewayResult.ProviderReference, cancellationToken);
        }

        return new FinanceCheckoutDto(
            checkout.TransactionId,
            PaymentPurposes.RentalFee,
            request.FeeItemId,
            checkout.Provider,
            checkout.Amount,
            gatewayResult.PaymentUrl,
            BankTransferDto.From(gatewayResult.BankTransfer));
    }
}
