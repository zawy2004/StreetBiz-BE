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

/// <summary>
/// <c>BankTransfer</c> is set for a bank-transfer provider (SEPAY): there is no page to send the
/// payer to, so the screen shows the VietQR and these details instead of redirecting.
/// </summary>
public sealed record PaymentGatewayCheckoutResult(
    string PaymentUrl,
    string? ProviderReference,
    BankTransferInstructions? BankTransfer = null);

/// <summary>
/// What the payer types into (or scans with) their banking app. <c>TransferContent</c> carries
/// the payment code that matches the incoming transfer back to its transaction.
/// </summary>
public sealed record BankTransferInstructions(
    string BankName,
    string AccountNumber,
    string? AccountName,
    decimal Amount,
    string TransferContent,
    string QrImageUrl);

/// <summary>
/// The parts of a callback request a provider authenticates with: MoMo/ZaloPay sign with
/// <c>Signature</c>; SePay sends <c>X-SePay-Signature</c> with <c>X-SePay-Timestamp</c>
/// (HMAC) or an <c>Authorization: Apikey …</c> header.
/// </summary>
public sealed record PaymentCallbackHeaders(
    string? Signature,
    string? Timestamp = null,
    string? Authorization = null);

public sealed record PaymentGatewayCallback(
    string? ProviderReference,
    string? IdempotencyKey,
    decimal? Amount,
    string? Status,
    bool SignatureValid,
    /// <summary>What the provider actually sent, when it differs from the request body (status queries).</summary>
    string? RawPayload = null,
    /// <summary>
    /// The provider's own id for this money movement (SePay's transaction id). Two reports with
    /// the same id are one transfer sent twice; a different id is a second transfer.
    /// </summary>
    string? ProviderEventId = null,
    /// <summary>
    /// A genuine notification that is not about a StreetBiz payment (money leaving the account,
    /// another linked account): acknowledged, not recorded.
    /// </summary>
    bool Ignored = false);

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
        PaymentCallbackHeaders? headers,
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

    /// <summary>
    /// The transfer details for a still-open bank-transfer payment, so its page can show the
    /// QR again after a reload. Null for providers that are not paid by bank transfer.
    /// </summary>
    BankTransferInstructions? DescribeBankTransfer(string provider, string providerReference, decimal amount) =>
        null;
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
