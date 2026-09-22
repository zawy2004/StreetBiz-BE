namespace StreetBiz.Application.Common.Interfaces;

public sealed record PaymentGatewayCheckoutRequest(
    long OrderId,
    string OrderCode,
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
    /// <summary>
    /// Whether this provider can currently start a checkout. Checked before the order
    /// is written, so a provider that is switched off does not leave the customer with
    /// an unpayable PENDING_PAYMENT order.
    /// </summary>
    bool IsProviderAvailable(string provider);

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
