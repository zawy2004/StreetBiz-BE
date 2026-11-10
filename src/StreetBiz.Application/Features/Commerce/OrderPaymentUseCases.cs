using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Commerce;
using StreetBiz.Application.DTOs.Payments;
using StreetBiz.Application.Features.Commerce.OrderTracking;
using StreetBiz.Application.Features.PayoutAccounts;

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

/// <summary>
/// PAYOS pays the storefront's household straight into its own payOS account: a household that has
/// not connected an ACTIVE receiving account takes no online orders.
/// </summary>
public sealed class CheckoutOrderCommandHandler(
    ICustomerContext customerContext,
    ICommerceRepository repository,
    IPaymentGateway paymentGateway,
    IPayeeAccountRepository? payees = null,
    PayeeCheckouts? payeeCheckouts = null) : IRequestHandler<CheckoutOrderCommand, CheckoutDto>
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

        // Checked before the order is written, so a store that cannot be paid leaves no unpayable order behind.
        PayeeSummary? payee = null;
        if (provider == PaymentProviders.PayOs)
        {
            payee = payees is null ? null : await payees.GetForCartAsync(request.CartId, customerUserId, cancellationToken);
            if (payee is not { Active: true } || payeeCheckouts is null)
            {
                throw new DomainRuleException(CommerceMessages.StoreNotAcceptingOnlinePayment);
            }
        }

        var mutation = await repository.CheckoutAsync(
            customerUserId,
            request.CartId,
            provider,
            idempotencyKey,
            cancellationToken);
        var order = mutation.RequireOrder();
        var row = mutation.Order!;
        // PAY-02: the same Idempotency-Key replays the order it created, whatever became of it. A replay
        // of a payment that already failed (or was paid) must not open a fresh payment page for that
        // dead order: money taken for an order that is no longer waiting would be refused by the
        // callback. Retrying a failed payment means a new checkout with a new key (a new order).
        if (row.OrderStatus != OrderStatuses.PendingPayment)
        {
            throw new ConflictException(row.OrderStatus == OrderStatuses.Cancelled
                ? CommerceMessages.CheckoutPaymentEnded
                : CommerceMessages.CheckoutAlreadyPaid);
        }

        if (!row.PaymentTransactionId.HasValue
            || !row.PaymentAmount.HasValue
            || string.IsNullOrWhiteSpace(row.PaymentIdempotencyKey)
            || string.IsNullOrWhiteSpace(row.PaymentProvider))
        {
            throw new InvalidOperationException("The order payment transaction was not created.");
        }

        var gatewayRequest = new PaymentGatewayCheckoutRequest(
            row.OrderId,
            row.OrderCode,
            row.PaymentTransactionId.Value,
            row.PaymentIdempotencyKey,
            row.PaymentProvider,
            row.PaymentAmount.Value);
        if (payee is not null && row.PaymentProvider == PaymentProviders.PayOs)
        {
            PaymentGatewayCheckoutResult payeeResult;
            try
            {
                payeeResult = await payeeCheckouts!.OpenAsync(payee, gatewayRequest, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The link could not be opened: release the order (and the cart) so the customer can try again.
                // A failure to release it must not hide why the payment could not start.
                try
                {
                    await repository.CancelCustomerOrderAsync(
                        customerUserId, row.OrderId, OrderStatuses.PendingPayment, CancellationToken.None);
                }
                catch (AppException)
                {
                }

                throw;
            }

            return new CheckoutDto(
                order.OrderId,
                order.OrderCode,
                order.OrderStatus,
                row.PaymentTransactionId.Value,
                row.PaymentProvider,
                row.PaymentAmount.Value,
                payeeResult.PaymentUrl,
                BankTransferDto.From(payeeResult.BankTransfer));
        }

        var gatewayResult = await paymentGateway.CreateCheckoutAsync(gatewayRequest, cancellationToken);
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
/// The VietQR of an order still waiting for its payOS bank transfer, so the payment page can
/// show it again after a reload or on another device. 404 once the order is no longer waiting.
/// </summary>
public sealed record GetOrderBankTransferQuery(long OrderId) : IRequest<BankTransferDto>;

public sealed class GetOrderBankTransferQueryHandler(
    ICustomerContext customerContext,
    ICommerceRepository repository,
    IPaymentGateway paymentGateway,
    IPaymentTransactionRepository? transactions = null) : IRequestHandler<GetOrderBankTransferQuery, BankTransferDto>
{
    public async Task<BankTransferDto> Handle(GetOrderBankTransferQuery request, CancellationToken cancellationToken)
    {
        var customerUserId = await customerContext.RequireCustomerUserIdAsync(cancellationToken);
        var order = await repository.GetCustomerOrderAsync(customerUserId, request.OrderId, cancellationToken)
            ?? throw new NotFoundException(CommerceMessages.OrderNotFound);
        BankTransferInstructions? instructions = null;
        if (order.OrderStatus == OrderStatuses.PendingPayment
            && order.PaymentProvider == PaymentProviders.PayOs
            && order.PaymentTransactionId is { } transactionId
            && transactions is not null)
        {
            // The payOS link opened at checkout, shown again while it is still payable.
            var stored = await transactions.GetAsync(transactionId, cancellationToken);
            instructions = stored is null
                ? null
                : paymentGateway.DescribeStoredCheckout(PaymentProviders.PayOs, stored.CheckoutPayload, stored.Amount);
        }

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
    IPaymentGateway paymentGateway,
    IPaymentTransactionRepository? transactions = null,
    PayeeCheckouts? payeeCheckouts = null) : IRequestHandler<SyncOrderPaymentCommand, OrderDto>
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

        // PAYOS: asked on the household's own channel, with its own keys.
        var payment = order.PaymentProvider == PaymentProviders.PayOs
            && order.PaymentTransactionId is { } transactionId
            && transactions is not null
                ? await transactions.GetAsync(transactionId, cancellationToken)
                : null;
        var status = payment?.PayeeAccountId is not null && payeeCheckouts is not null
            ? await payeeCheckouts.QueryAsync(payment, cancellationToken)
            : await paymentGateway.QueryPaymentAsync(order.PaymentProvider, order.PaymentProviderReference, cancellationToken);
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
                status.ProviderEventId)
            {
                PayeeAccountId = payment?.PayeeAccountId,
            },
            cancellationToken);
        var updated = await repository.GetCustomerOrderAsync(customerUserId, request.OrderId, cancellationToken);
        return (updated ?? order).ToDto();
    }
}

/// <summary><c>Signature</c> is a URL-template gateway's signature header; see <see cref="PaymentCallbackHeaders"/>.</summary>
public sealed record ProcessPaymentCallbackCommand(
    string Provider,
    string RawPayload,
    string? Signature) : IRequest<PaymentCallbackReceiptDto>
{
    /// <summary>
    /// PAYOS: the token in the webhook URL (/api/payments/payos/callback/{token}). It names the
    /// receiving account whose checksum key verifies the body and which alone may settle it.
    /// </summary>
    public string? PayeeToken { get; init; }
}

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
    IPaymentAlertNotifier? alerts = null,
    IPayeeAccountRepository? payees = null)
    : IRequestHandler<ProcessPaymentCallbackCommand, PaymentCallbackReceiptDto>
{
    public async Task<PaymentCallbackReceiptDto> Handle(
        ProcessPaymentCallbackCommand request,
        CancellationToken cancellationToken)
    {
        var provider = request.Provider.Trim().ToUpperInvariant();
        // PAYOS: the account is found from the URL, never from the body. An unknown token leaves
        // nothing to verify with, so the callback is recorded as REJECTED.
        var payee = provider == PaymentProviders.PayOs
            && payees is not null
            && !string.IsNullOrWhiteSpace(request.PayeeToken)
                ? await payees.FindByWebhookTokenAsync(request.PayeeToken, cancellationToken)
                : null;
        if (provider == PaymentProviders.PayOs && payee is null)
        {
            // No such receiving account: nothing to verify with and nothing it could settle. This is
            // also payOS's own test call while a channel is being connected (the account is saved
            // after payOS has accepted the URL), so it is answered but neither recorded nor alerted.
            return new PaymentCallbackReceiptDto("IGNORED", 0, null, null, null);
        }

        var verified = await paymentGateway.VerifyCallbackAsync(
            provider,
            request.RawPayload,
            new PaymentCallbackHeaders(request.Signature)
            {
                Payee = payee?.Credentials,
            },
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
            verified.ProviderEventId)
        {
            PayeeAccountId = payee?.Account.PayeeAccountId,
        };

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
            "Chữ ký không hợp lệ nên callback không được ghi nhận. " +
            "Kiểm tra khoá ký ở cả hai phía (cổng thanh toán và cấu hình máy chủ).",
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
