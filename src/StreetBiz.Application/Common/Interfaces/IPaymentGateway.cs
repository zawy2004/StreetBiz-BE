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
    decimal Amount,
    /// <summary>ORDER, RENTAL_FEE or PENALTY: decides which page the buyer returns to.</summary>
    string Purpose = "ORDER");

public sealed record PaymentGatewayCheckoutResult(
    string PaymentUrl,
    string? ProviderReference);

public sealed record PaymentGatewayCallback(
    string? ProviderReference,
    string? IdempotencyKey,
    decimal? Amount,
    string? Status,
    bool SignatureValid,
    /// <summary>What the provider actually sent, when it differs from the request body (status queries).</summary>
    string? RawPayload = null);

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

    /// <summary>
    /// Asks the provider directly for a transaction's state, for when its callback cannot
    /// reach us (local development) or was lost. Null when the provider has no status API
    /// here (sandbox, URL-template gateways); a null Status means "not final yet".
    /// </summary>
    Task<PaymentGatewayCallback?> QueryPaymentAsync(
        string provider,
        string providerReference,
        CancellationToken cancellationToken) =>
        Task.FromResult<PaymentGatewayCallback?>(null);
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
