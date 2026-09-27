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

/// <summary>
/// The buyer is back from the provider's page. Rather than trusting the return URL's query
/// string, ask the provider itself for the transaction state and apply it through the same
/// path as a verified callback. This is what settles payments when the provider's IPN cannot
/// reach the server (local development) or never arrives.
/// </summary>
public sealed record SyncOrderPaymentCommand(long OrderId) : IRequest<OrderDto>;

public sealed class SyncOrderPaymentCommandHandler(
    ICustomerContext customerContext,
    ICommerceRepository repository,
    IPaymentGateway paymentGateway) : IRequestHandler<SyncOrderPaymentCommand, OrderDto>
{
    public async Task<OrderDto> Handle(SyncOrderPaymentCommand request, CancellationToken cancellationToken)
    {
        var customerUserId = await customerContext.RequireCustomerUserIdAsync(cancellationToken);
        var order = await repository.GetCustomerOrderAsync(customerUserId, request.OrderId, cancellationToken)
            ?? throw new NotFoundException(CommerceMessages.OrderNotFound);
        if (order.OrderStatus != OrderStatuses.PendingPayment
            || string.IsNullOrWhiteSpace(order.PaymentProvider)
            || string.IsNullOrWhiteSpace(order.PaymentProviderReference))
        {
            return order.ToDto();
        }

        var status = await paymentGateway.QueryPaymentAsync(
            order.PaymentProvider, order.PaymentProviderReference, cancellationToken);
        if (status?.Status is null)
        {
            return order.ToDto();
        }

        await repository.ApplyPaymentCallbackAsync(
            new PaymentCallbackData(
                order.PaymentProvider,
                status.ProviderReference ?? order.PaymentProviderReference,
                order.PaymentIdempotencyKey,
                status.Amount,
                status.Status,
                status.RawPayload ?? "{}",
                status.SignatureValid),
            cancellationToken);
        var updated = await repository.GetCustomerOrderAsync(customerUserId, request.OrderId, cancellationToken);
        return (updated ?? order).ToDto();
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
