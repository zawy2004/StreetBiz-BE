using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
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
        if (!paymentGateway.IsProviderAvailable(provider))
        {
            throw new DomainRuleException($"Payment provider {provider} is not configured.");
        }

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

public sealed class ProcessPaymentCallbackCommandHandler(
    IPaymentGateway paymentGateway,
    ICommerceRepository repository)
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
        var result = await repository.ApplyPaymentCallbackAsync(
            new PaymentCallbackData(
                provider,
                verified.ProviderReference,
                verified.IdempotencyKey,
                verified.Amount,
                verified.Status,
                request.RawPayload,
                verified.SignatureValid),
            cancellationToken);
        return new PaymentCallbackReceiptDto(
            result.Outcome.ToString().ToUpperInvariant(),
            result.CallbackEventId,
            result.TransactionId,
            result.OrderId,
            result.OrderStatus);
    }
}
