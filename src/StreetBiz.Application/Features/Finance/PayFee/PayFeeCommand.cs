using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Finance;

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
            .WithMessage("Provider must be MOMO or ZALOPAY.");
        RuleFor(x => x.IdempotencyKey).NotEmpty().MaximumLength(100);
    }
}

public sealed class PayFeeCommandHandler(
    IVendorContext vendorContext,
    IFinanceRepository finance,
    IPaymentGateway paymentGateway) : IRequestHandler<PayFeeCommand, FinanceCheckoutDto>
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

        var checkout = await finance.CreateFeeCheckoutAsync(
            vendorId, request.FeeItemId, provider, idempotencyKey, cancellationToken);

        var gatewayResult = await paymentGateway.CreateCheckoutAsync(
            new PaymentGatewayCheckoutRequest(
                request.FeeItemId,
                item.FeeItemPeriodLabel(),
                checkout.TransactionId,
                checkout.IdempotencyKey,
                checkout.Provider,
                checkout.Amount),
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(gatewayResult.ProviderReference))
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
            gatewayResult.PaymentUrl);
    }
}
