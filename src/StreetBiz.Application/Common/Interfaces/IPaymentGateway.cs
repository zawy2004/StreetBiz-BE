namespace StreetBiz.Application.Common.Interfaces;

/// <summary>
/// What the gateway needs to start a checkout, for any payment purpose (RENTAL_FEE, PENALTY or
/// ORDER). <c>ReferenceId</c>/<c>ReferenceCode</c> identify the thing being paid for to the
/// vendor/customer — an order id/code today, a fee item or penalty id in the finance module —
/// and are only ever used to build a human-facing checkout URL, never to look anything up.
/// </summary>
public sealed record PaymentGatewayCheckoutRequest(
    long ReferenceId,
    string ReferenceCode,
    long TransactionId,
    string IdempotencyKey,
    string Provider,
    decimal Amount);

public sealed record PaymentGatewayCheckoutResult(
    string PaymentUrl,
    string? ProviderReference);

public sealed record PaymentGatewayCallback(
    string? ProviderReference,
    string? IdempotencyKey,
    decimal? Amount,
    string? Status,
    bool SignatureValid);

public interface IPaymentGateway
{
    Task<PaymentGatewayCheckoutResult> CreateCheckoutAsync(
        PaymentGatewayCheckoutRequest request,
        CancellationToken cancellationToken);

    Task<PaymentGatewayCallback> VerifyCallbackAsync(
        string provider,
        string rawPayload,
        string? signature,
        CancellationToken cancellationToken);
}

public sealed record RefundGatewayRequest(
    long RefundId,
    long OrderId,
    long PaymentTransactionId,
    string Provider,
    decimal Amount,
    string IdempotencyKey);

public sealed record RefundGatewayResult(bool Accepted, string? ProviderReference);

public interface IRefundGateway
{
    Task<RefundGatewayResult> RequestRefundAsync(
        RefundGatewayRequest request,
        CancellationToken cancellationToken);
}
