using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Finance;

namespace StreetBiz.Application.Features.Finance.PayPenalty;

/// <summary>FEE-04: open a checkout for one penalty.</summary>
public sealed record PayPenaltyCommand(long PenaltyId, string Provider, string IdempotencyKey)
    : IRequest<FinanceCheckoutDto>;

public sealed class PayPenaltyCommandValidator : AbstractValidator<PayPenaltyCommand>
{
    public PayPenaltyCommandValidator()
    {
        RuleFor(x => x.PenaltyId).GreaterThan(0);
        RuleFor(x => x.Provider)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .Must(value => PaymentProviders.IsValid(value.Trim().ToUpperInvariant()))
            .WithMessage("Provider must be MOMO or ZALOPAY.");
        RuleFor(x => x.IdempotencyKey).NotEmpty().MaximumLength(100);
    }
}

public sealed class PayPenaltyCommandHandler(
    IVendorContext vendorContext,
    IFinanceRepository finance,
    IPaymentGateway paymentGateway) : IRequestHandler<PayPenaltyCommand, FinanceCheckoutDto>
{
    public async Task<FinanceCheckoutDto> Handle(PayPenaltyCommand request, CancellationToken cancellationToken)
    {
        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);
        var provider = request.Provider.Trim().ToUpperInvariant();
        var idempotencyKey = request.IdempotencyKey.Trim();

        var penalty = await finance.GetPenaltyForCheckoutAsync(vendorId, request.PenaltyId, cancellationToken)
            ?? throw new NotFoundException(FinanceMessages.PenaltyNotFound);
        if (!PenaltyStatuses.IsPayable(penalty.PenaltyStatus))
        {
            throw new DomainRuleException(FinanceMessages.PenaltyNotPayable);
        }

        var checkout = await finance.CreatePenaltyCheckoutAsync(
            vendorId, request.PenaltyId, provider, idempotencyKey, cancellationToken);

        var gatewayResult = await paymentGateway.CreateCheckoutAsync(
            new PaymentGatewayCheckoutRequest(
                request.PenaltyId,
                penalty.ViolationLabel,
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
            PaymentPurposes.Penalty,
            request.PenaltyId,
            checkout.Provider,
            checkout.Amount,
            gatewayResult.PaymentUrl);
    }
}
