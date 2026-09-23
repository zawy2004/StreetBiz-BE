using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Commerce;

namespace StreetBiz.Application.Features.Commerce;

public sealed record CheckoutOrderCommand(
    long CartId,
    string Provider,
    string IdempotencyKey) : IRequest<CheckoutDto>;

public sealed class CheckoutOrderCommandValidator : AbstractValidator<CheckoutOrderCommand>
{
    public CheckoutOrderCommandValidator()
    {
        RuleFor(x => x.CartId).GreaterThan(0);
        RuleFor(x => x.Provider)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .Must(value => PaymentProviders.IsValid(value.Trim().ToUpperInvariant()))
            .WithMessage("Provider must be MOMO or ZALOPAY.");
        RuleFor(x => x.IdempotencyKey).NotEmpty().MaximumLength(100);
    }
}

public sealed class CheckoutOrderCommandHandler(
    ICustomerContext customerContext,
    ICommerceRepository repository,
    IPaymentGateway paymentGateway) : IRequestHandler<CheckoutOrderCommand, CheckoutDto>
{
    public async Task<CheckoutDto> Handle(
        CheckoutOrderCommand request,
        CancellationToken cancellationToken)
    {
        var customerUserId = await customerContext.RequireCustomerUserIdAsync(cancellationToken);
        var provider = request.Provider.Trim().ToUpperInvariant();
        var idempotencyKey = request.IdempotencyKey.Trim();
        var mutation = await repository.CheckoutAsync(
            customerUserId,
            request.CartId,
            provider,
            idempotencyKey,
            cancellationToken);
        var order = mutation.RequireOrder();
        var row = mutation.Order!;
        if (!row.PaymentTransactionId.HasValue
            || !row.PaymentAmount.HasValue
            || string.IsNullOrWhiteSpace(row.PaymentIdempotencyKey)
            || string.IsNullOrWhiteSpace(row.PaymentProvider))
        {
            throw new InvalidOperationException("The order payment transaction was not created.");
        }

        var gatewayResult = await paymentGateway.CreateCheckoutAsync(
            new PaymentGatewayCheckoutRequest(
                row.OrderId,
                row.OrderCode,
                row.PaymentTransactionId.Value,
                row.PaymentIdempotencyKey,
                row.PaymentProvider,
                row.PaymentAmount.Value),
            cancellationToken);
        if (!string.IsNullOrWhiteSpace(gatewayResult.ProviderReference))
        {
            await repository.SetPaymentProviderReferenceAsync(
                row.PaymentTransactionId.Value,
                gatewayResult.ProviderReference,
                cancellationToken);
        }

        return new CheckoutDto(
            order.OrderId,
            order.OrderCode,
            order.OrderStatus,
            row.PaymentTransactionId.Value,
            row.PaymentProvider,
            row.PaymentAmount.Value,
            gatewayResult.PaymentUrl);
    }
}

public sealed record ProcessPaymentCallbackCommand(
    string Provider,
    string RawPayload,
    string? Signature) : IRequest<PaymentCallbackReceiptDto>;

public sealed class ProcessPaymentCallbackCommandValidator
    : AbstractValidator<ProcessPaymentCallbackCommand>
{
    public ProcessPaymentCallbackCommandValidator()
    {
        RuleFor(x => x.Provider)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .Must(value => PaymentProviders.IsValid(value.Trim().ToUpperInvariant()))
            .WithMessage("Provider must be MOMO or ZALOPAY.");
        RuleFor(x => x.RawPayload).NotEmpty().MaximumLength(1_000_000);
    }
}

/// <summary>
/// SYS-04's one entry point for every provider: a real MoMo/ZaloPay merchant account has exactly
/// one configured webhook URL, so RENTAL_FEE, PENALTY and ORDER callbacks must all land here.
/// <see cref="StreetBiz.Application.Common.Interfaces.IFinanceRepository.FindPaymentPurposeAsync"/>
/// is a read-only lookup by the transaction's own provider reference/idempotency key — the same
/// keys used to match it below — so routing never guesses and never writes before dispatch.
/// Exactly one repository's ApplyPaymentCallbackAsync runs per callback, so exactly one
/// PaymentCallbackEvents row is written, whichever purpose owns it.
/// </summary>
public sealed class ProcessPaymentCallbackCommandHandler(
    IPaymentGateway paymentGateway,
    ICommerceRepository repository,
    IFinanceRepository finance)
    : IRequestHandler<ProcessPaymentCallbackCommand, PaymentCallbackReceiptDto>
{
    public async Task<PaymentCallbackReceiptDto> Handle(
        ProcessPaymentCallbackCommand request,
        CancellationToken cancellationToken)
    {
        var provider = request.Provider.Trim().ToUpperInvariant();
        var verified = await paymentGateway.VerifyCallbackAsync(
            provider,
            request.RawPayload,
            request.Signature,
            cancellationToken);
        var data = new PaymentCallbackData(
            provider,
            verified.ProviderReference,
            verified.IdempotencyKey,
            verified.Amount,
            verified.Status,
            request.RawPayload,
            verified.SignatureValid);

        var purpose = await finance.FindPaymentPurposeAsync(
            verified.ProviderReference, verified.IdempotencyKey, cancellationToken);
        if (PaymentPurposes.IsFinance(purpose))
        {
            var financeResult = await finance.ApplyPaymentCallbackAsync(data, cancellationToken);
            return new PaymentCallbackReceiptDto(
                financeResult.Outcome.ToString().ToUpperInvariant(),
                financeResult.CallbackEventId,
                financeResult.TransactionId,
                null,
                null);
        }

        var result = await repository.ApplyPaymentCallbackAsync(data, cancellationToken);
        return new PaymentCallbackReceiptDto(
            result.Outcome.ToString().ToUpperInvariant(),
            result.CallbackEventId,
            result.TransactionId,
            result.OrderId,
            result.OrderStatus);
    }
}
