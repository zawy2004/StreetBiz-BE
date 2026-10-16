using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Commerce;
using StreetBiz.Application.DTOs.Payments;
using StreetBiz.Application.Features.Commerce.OrderTracking;

namespace StreetBiz.Application.Features.Commerce;

public sealed record CheckoutOrderCommand(
    long CartId,
    string Provider,
    string IdempotencyKey,
    PickupLocationInput? Location = null) : IRequest<CheckoutDto>, IPickupRangeGated
{
    long? IPickupRangeGated.PickupCartId => CartId;
}

public sealed class CheckoutOrderCommandValidator : AbstractValidator<CheckoutOrderCommand>
{
    public CheckoutOrderCommandValidator()
    {
        RuleFor(x => x.CartId).GreaterThan(0);
        RuleFor(x => x.Provider)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .Must(value => PaymentProviders.IsValid(value.Trim().ToUpperInvariant()))
            .WithMessage(PaymentProviders.InvalidMessage);
        RuleFor(x => x.IdempotencyKey).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Location!).SetValidator(new PickupLocationInputValidator()).When(x => x.Location is not null);
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
            gatewayResult.PaymentUrl,
            BankTransferDto.From(gatewayResult.BankTransfer));
    }
}

/// <summary>
/// The VietQR of an order still waiting for its SePay bank transfer, so the payment page can
/// show it again after a reload or on another device. 404 once the order is no longer waiting.
/// </summary>
public sealed record GetOrderBankTransferQuery(long OrderId) : IRequest<BankTransferDto>;

public sealed class GetOrderBankTransferQueryHandler(
    ICustomerContext customerContext,
    ICommerceRepository repository,
    IPaymentGateway paymentGateway) : IRequestHandler<GetOrderBankTransferQuery, BankTransferDto>
{
    public async Task<BankTransferDto> Handle(GetOrderBankTransferQuery request, CancellationToken cancellationToken)
    {
        var customerUserId = await customerContext.RequireCustomerUserIdAsync(cancellationToken);
        var order = await repository.GetCustomerOrderAsync(customerUserId, request.OrderId, cancellationToken)
            ?? throw new NotFoundException(CommerceMessages.OrderNotFound);
        var instructions = order.OrderStatus == OrderStatuses.PendingPayment
            && order.PaymentProvider is { } provider
            && order.PaymentProviderReference is { } reference
            && order.PaymentAmount is { } amount
                ? paymentGateway.DescribeBankTransfer(provider, reference, amount)
                : null;
        return BankTransferDto.From(instructions)
            ?? throw new NotFoundException("Đơn hàng này không chờ chuyển khoản.");
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
                status.SignatureValid,
                status.ProviderEventId),
            cancellationToken);
        var updated = await repository.GetCustomerOrderAsync(customerUserId, request.OrderId, cancellationToken);
        return (updated ?? order).ToDto();
    }
}

/// <summary><c>Timestamp</c>/<c>Authorization</c> carry SePay's webhook authentication; see <see cref="PaymentCallbackHeaders"/>.</summary>
public sealed record ProcessPaymentCallbackCommand(
    string Provider,
    string RawPayload,
    string? Signature,
    string? Timestamp = null,
    string? Authorization = null) : IRequest<PaymentCallbackReceiptDto>;

public sealed class ProcessPaymentCallbackCommandValidator
    : AbstractValidator<ProcessPaymentCallbackCommand>
{
    public ProcessPaymentCallbackCommandValidator()
    {
        RuleFor(x => x.Provider)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .Must(value => PaymentProviders.IsValid(value.Trim().ToUpperInvariant()))
            .WithMessage(PaymentProviders.InvalidMessage);
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
/// A callback that could not be applied as it stands (no match, wrong amount, bad signature,
/// money for something already settled) or that failed with an error is also sent to
/// <see cref="IPaymentAlertNotifier"/>, so a person sees it the same minute.
/// </summary>
public sealed class ProcessPaymentCallbackCommandHandler(
    IPaymentGateway paymentGateway,
    ICommerceRepository repository,
    IFinanceRepository finance,
    IPaymentAlertNotifier? alerts = null)
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
            new PaymentCallbackHeaders(request.Signature, request.Timestamp, request.Authorization),
            cancellationToken);
        if (verified.Ignored && verified.SignatureValid)
        {
            // e.g. money leaving the ward's account: genuine, but not a StreetBiz payment.
            return new PaymentCallbackReceiptDto("IGNORED", 0, null, null, null);
        }

        var data = new PaymentCallbackData(
            provider,
            verified.ProviderReference,
            verified.IdempotencyKey,
            verified.Amount,
            verified.Status,
            request.RawPayload,
            verified.SignatureValid,
            verified.ProviderEventId);

        PaymentCallbackReceiptDto receipt;
        PaymentCallbackOutcome outcome;
        try
        {
            var purpose = await finance.FindPaymentPurposeAsync(
                verified.ProviderReference, verified.IdempotencyKey, cancellationToken);
            if (PaymentPurposes.IsFinance(purpose))
            {
                var financeResult = await finance.ApplyPaymentCallbackAsync(data, cancellationToken);
                outcome = financeResult.Outcome;
                receipt = new PaymentCallbackReceiptDto(
                    financeResult.Outcome.ToString().ToUpperInvariant(),
                    financeResult.CallbackEventId,
                    financeResult.TransactionId,
                    null,
                    null);
            }
            else
            {
                var result = await repository.ApplyPaymentCallbackAsync(data, cancellationToken);
                outcome = result.Outcome;
                receipt = new PaymentCallbackReceiptDto(
                    result.Outcome.ToString().ToUpperInvariant(),
                    result.CallbackEventId,
                    result.TransactionId,
                    result.OrderId,
                    result.OrderStatus);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await AlertAsync(data, "ERROR",
                $"Lỗi máy chủ khi xử lý callback ({exception.GetType().Name}): {exception.Message}. " +
                "Nhà cung cấp sẽ gửi lại; nếu lỗi kéo dài, kiểm tra log API.",
                null, null, CancellationToken.None);
            throw;
        }

        if (ReasonFor(outcome, data.SignatureValid) is { } reason)
        {
            await AlertAsync(data, receipt.Result, reason, receipt.TransactionId, receipt.CallbackEventId, cancellationToken);
        }

        return receipt;
    }

    private static string? ReasonFor(PaymentCallbackOutcome outcome, bool signatureValid) => outcome switch
    {
        PaymentCallbackOutcome.Rejected when !signatureValid =>
            "Chữ ký hoặc API key không hợp lệ nên callback không được ghi nhận. " +
            "Kiểm tra secret/API key ở cả hai phía (SePay và user-secrets).",
        PaymentCallbackOutcome.Rejected =>
            "Số tiền, trạng thái hoặc cổng thanh toán không khớp giao dịch, hoặc đơn không còn chờ thanh toán. " +
            "Chưa ghi nhận: đối soát với sao kê rồi xử lý thủ công.",
        PaymentCallbackOutcome.Unmatched =>
            "Không có giao dịch StreetBiz nào khớp mã thanh toán (người chuyển ghi thiếu hoặc sai nội dung). " +
            "Tiền đã vào tài khoản nhưng chưa được gán cho khoản nào.",
        PaymentCallbackOutcome.Surplus =>
            "Tiền về cho khoản đã thanh toán hoặc đơn đã đóng (chuyển hai lần, hoặc chuyển sau khi đơn bị huỷ). " +
            "Không áp dụng; cần hoàn tiền thủ công cho người chuyển.",
        _ => null,
    };

    private Task AlertAsync(
        PaymentCallbackData data,
        string outcome,
        string reason,
        long? transactionId,
        long? callbackEventId,
        CancellationToken cancellationToken) =>
        alerts?.NotifyAsync(
            new PaymentAlert(
                data.Provider,
                outcome,
                reason,
                data.ProviderReference,
                data.Amount,
                data.ProviderEventId,
                transactionId,
                callbackEventId),
            cancellationToken) ?? Task.CompletedTask;
}
