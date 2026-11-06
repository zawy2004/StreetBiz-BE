namespace StreetBiz.Infrastructure.Payments;

public sealed class PaymentGatewaySettings
{
    public const string SectionName = "Payments";
    public bool SandboxEnabled { get; set; }
    public PaymentProviderSettings Momo { get; set; } = new();
    public PaymentProviderSettings ZaloPay { get; set; } = new();
    public SePaySettings SePay { get; set; } = new();
    public PayOsSettings PayOs { get; set; } = new();

    public PaymentProviderSettings For(string provider) => provider switch
    {
        "MOMO" => Momo,
        "ZALOPAY" => ZaloPay,
        _ => new PaymentProviderSettings(),
    };
}

/// <summary>
/// payOS (https://payos.vn/docs/api/): every household and ward connects its own payOS channel,
/// so the payer's VietQR transfer goes straight into the payee's account and StreetBiz never
/// holds the money. The channel keys are stored per payee (encrypted with
/// <see cref="CredentialKey"/>), not here.
/// </summary>
public sealed class PayOsSettings
{
    /// <summary>Off switches PAYOS off everywhere, whatever the payees have connected.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>payOS's merchant API. Point it at scripts/payos-fake for a demo without real money.</summary>
    public string ApiBaseUrl { get; set; } = "https://api-merchant.payos.vn";

    /// <summary>
    /// The public https origin payOS can reach (ngrok, the deployed API), e.g. https://abc.ngrok-free.app.
    /// Empty: the webhook is not registered and payments are settled by asking payOS instead.
    /// </summary>
    public string? PublicWebhookBaseUrl { get; set; }

    /// <summary>Where payOS's hosted page sends the payer back (the web app), e.g. http://localhost:5173.</summary>
    public string FrontendBaseUrl { get; set; } = "http://localhost:5173";

    /// <summary>How long a payment link (and its QR) stays payable.</summary>
    public int LinkLifetimeMinutes { get; set; } = 30;

    /// <summary>
    /// Base64 of 32 random bytes that encrypts the payees' keys (AES-GCM). User-secrets / App
    /// Settings only. Outside Development, PAYOS stays off until it is set.
    /// </summary>
    public string? CredentialKey { get; set; }
}

/// <summary>
/// SePay (https://developer.sepay.vn): the payer transfers to <c>AccountNumber</c> by VietQR
/// with the payment code in the content, and SePay's webhook reports the money arriving.
/// Secrets (<c>WebhookSecret</c>, <c>WebhookApiKey</c>, <c>ApiToken</c>) belong in user-secrets
/// or environment variables, never in a committed appsettings file.
/// </summary>
public sealed class SePaySettings
{
    /// <summary>The bank as SePay's QR service names it, e.g. "MBBank", "Vietcombank", "TPBank".</summary>
    public string? BankName { get; set; }

    public string? AccountNumber { get; set; }

    /// <summary>Shown to the payer so they can check who they are paying.</summary>
    public string? AccountName { get; set; }

    /// <summary>
    /// The payment code is this prefix plus the transaction id (SBP42). Set the same prefix in
    /// My SePay → Cấu hình công ty → Cấu trúc mã thanh toán so SePay fills the webhook's "code".
    /// </summary>
    public string PaymentCodePrefix { get; set; } = "SBP";

    /// <summary>The transfer content; "{code}" is replaced by the payment code. Some banks need a fixed prefix.</summary>
    public string TransferContentTemplate { get; set; } = "{code}";

    /// <summary>The webhook's HMAC-SHA256 secret key (recommended authentication).</summary>
    public string? WebhookSecret { get; set; }

    /// <summary>The webhook's API key, sent by SePay as "Authorization: Apikey {key}".</summary>
    public string? WebhookApiKey { get; set; }

    /// <summary>
    /// A My SePay API token (Cấu hình công ty → API Access). Optional: with it, "Kiểm tra lại" and
    /// the stale-payment sweep can ask SePay directly when the webhook cannot reach the server.
    /// </summary>
    public string? ApiToken { get; set; }

    public string ApiBaseUrl { get; set; } = "https://my.sepay.vn/userapi";

    public string QrImageBaseUrl { get; set; } = "https://qr.sepay.vn/img";

    /// <summary>How far X-SePay-Timestamp may be from now before an HMAC-signed webhook is refused.</summary>
    public int SignatureToleranceSeconds { get; set; } = 300;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BankName)
        && !string.IsNullOrWhiteSpace(AccountNumber)
        && !string.IsNullOrWhiteSpace(PaymentCodePrefix)
        && (!string.IsNullOrWhiteSpace(WebhookSecret)
            || !string.IsNullOrWhiteSpace(WebhookApiKey)
            || !string.IsNullOrWhiteSpace(ApiToken));
}

public sealed class PaymentProviderSettings
{
    public string? CheckoutUrlTemplate { get; set; }
    public string? CallbackSecret { get; set; }

    // ---- MoMo All-In-One v2 (https://developers.momo.vn). Keys belong in
    // appsettings.Development.json / user-secrets, never in the committed appsettings.json.

    /// <summary>v2 gateway base, e.g. https://test-payment.momo.vn/v2/gateway/api (…/create, …/query).</summary>
    public string? ApiBaseUrl { get; set; }
    public string? PartnerCode { get; set; }
    public string? AccessKey { get; set; }
    public string? SecretKey { get; set; }

    /// <summary>
    /// Where MoMo sends the buyer back. <c>{referenceId}</c> is replaced with the order id,
    /// e.g. http://localhost:5173/customer/orders/{referenceId}/payment.
    /// </summary>
    public string? ReturnUrl { get; set; }

    /// <summary>Return page for a rental-fee instalment; <c>{referenceId}</c> is the fee item id.</summary>
    public string? FeeReturnUrl { get; set; }

    /// <summary>Return page for a penalty; <c>{referenceId}</c> is the penalty id.</summary>
    public string? PenaltyReturnUrl { get; set; }

    /// <summary>The page the buyer lands on after MoMo, by payment purpose (falls back to ReturnUrl).</summary>
    public string ReturnUrlFor(string purpose) => purpose switch
    {
        "RENTAL_FEE" when !string.IsNullOrWhiteSpace(FeeReturnUrl) => FeeReturnUrl,
        "PENALTY" when !string.IsNullOrWhiteSpace(PenaltyReturnUrl) => PenaltyReturnUrl,
        _ => ReturnUrl!,
    };

    /// <summary>
    /// Public URL of POST /api/payments/momo/callback (the IPN). MoMo cannot reach localhost,
    /// so locally the payment screen asks the backend to query MoMo instead.
    /// </summary>
    public string? NotifyUrl { get; set; }

    /// <summary>v2 request type; "captureWallet" opens the MoMo wallet / QR page.</summary>
    public string RequestType { get; set; } = "captureWallet";

    // ---- ZaloPay Gateway v2 (https://docs.zalopay.vn). Switched off until Enabled is true AND every
    // credential below is present, so a half-filled section never starts a checkout. Keys belong in
    // user-secrets / environment variables, never in the committed appsettings.json.
    // ApiBaseUrl, ReturnUrl, FeeReturnUrl and PenaltyReturnUrl are shared with MoMo's fields above.

    /// <summary>The explicit switch for ZaloPay; false by default.</summary>
    public bool Enabled { get; set; }

    /// <summary>ZaloPay's numeric app_id (kept as text: it is only ever sent and compared as text).</summary>
    public string? AppId { get; set; }

    /// <summary>Signs create/query requests.</summary>
    public string? Key1 { get; set; }

    /// <summary>Verifies the callback ZaloPay sends back.</summary>
    public string? Key2 { get; set; }

    /// <summary>Public URL of POST /api/payments/zalopay/callback. ZaloPay cannot reach localhost, so locally the payment screen asks the backend to query ZaloPay instead.</summary>
    public string? CallbackUrl { get; set; }

    public bool IsZaloPayConfigured =>
        Enabled
        && !string.IsNullOrWhiteSpace(ApiBaseUrl)
        && !string.IsNullOrWhiteSpace(AppId)
        && !string.IsNullOrWhiteSpace(Key1)
        && !string.IsNullOrWhiteSpace(Key2)
        && !string.IsNullOrWhiteSpace(ReturnUrl)
        && !string.IsNullOrWhiteSpace(CallbackUrl);

    public bool IsMomoConfigured =>
        !string.IsNullOrWhiteSpace(ApiBaseUrl)
        && !string.IsNullOrWhiteSpace(PartnerCode)
        && !string.IsNullOrWhiteSpace(AccessKey)
        && !string.IsNullOrWhiteSpace(SecretKey)
        && !string.IsNullOrWhiteSpace(ReturnUrl)
        && !string.IsNullOrWhiteSpace(NotifyUrl);
}
