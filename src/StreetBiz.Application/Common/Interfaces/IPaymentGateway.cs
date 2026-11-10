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
    string Purpose = "ORDER")
{
    /// <summary>
    /// PAYOS: the channel the money goes to (the ward for a fee, the household for an order).
    /// The link is created with these keys, so the transfer lands in the payee's own account.
    /// </summary>
    public PayeeCredentials? Payee { get; init; }

    /// <summary>PAYOS: what an earlier attempt for this transaction stored, so the next attempt gets a new link code.</summary>
    public string? PreviousCheckoutPayload { get; init; }
}

/// <summary>
/// <c>BankTransfer</c> is set for a bank-transfer provider (PAYOS): the screen shows the VietQR
/// and these details on the page itself instead of redirecting.
/// </summary>
public sealed record PaymentGatewayCheckoutResult(
    string PaymentUrl,
    string? ProviderReference,
    BankTransferInstructions? BankTransfer = null)
{
    /// <summary>PAYOS: the link as the provider returned it, stored so the same QR is shown after a reload.</summary>
    public string? CheckoutPayload { get; init; }
}

/// <summary>
/// What the payer types into (or scans with) their banking app. <c>TransferContent</c> carries
/// the payment code that matches the incoming transfer back to its transaction.
/// </summary>
public sealed record BankTransferInstructions(
    string BankName,
    string AccountNumber,
    string? AccountName,
    decimal Amount,
    string TransferContent)
{
    /// <summary>The EMVCo VietQR content to draw as a QR code.</summary>
    public string? QrContent { get; init; }

    /// <summary>PAYOS: the provider's hosted payment page, for a payer who prefers it.</summary>
    public string? CheckoutUrl { get; init; }
}

/// <summary>
/// The parts of a callback request a provider authenticates with beyond its body:
/// <c>Signature</c> for a URL-template gateway's HMAC header (MoMo and ZaloPay sign inside
/// the body itself).
/// </summary>
public sealed record PaymentCallbackHeaders(string? Signature)
{
    /// <summary>
    /// PAYOS: the keys of the channel the webhook URL belongs to. payOS signs the body with that
    /// channel's checksum key, so the right key is found from the URL, never from the body.
    /// </summary>
    public PayeeCredentials? Payee { get; init; }
}

public sealed record PaymentGatewayCallback(
    string? ProviderReference,
    string? IdempotencyKey,
    decimal? Amount,
    string? Status,
    bool SignatureValid,
    /// <summary>What the provider actually sent, when it differs from the request body (status queries).</summary>
    string? RawPayload = null,
    /// <summary>
    /// The provider's own id for this money movement (payOS's bank reference). Two reports with
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
    /// The state of a payment made to a payee's own channel (PAYOS), asked with that payee's keys.
    /// Null when the provider has no such channel here.
    /// </summary>
    Task<PaymentGatewayCallback?> QueryPaymentAsync(
        string provider,
        string providerReference,
        PayeeCredentials payee,
        CancellationToken cancellationToken) =>
        QueryPaymentAsync(provider, providerReference, cancellationToken);

    /// <summary>
    /// The transfer details kept from a checkout (PAYOS stores its link), so the page can show the
    /// same QR again. Null once the stored link has expired, or for providers that store nothing.
    /// </summary>
    BankTransferInstructions? DescribeStoredCheckout(string provider, string? checkoutPayload, decimal amount) =>
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
