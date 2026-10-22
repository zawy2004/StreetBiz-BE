namespace StreetBiz.Infrastructure.Payments;

public sealed class PaymentGatewaySettings
{
    public const string SectionName = "Payments";
    public bool SandboxEnabled { get; set; }
    public PaymentProviderSettings Momo { get; set; } = new();
    public PaymentProviderSettings ZaloPay { get; set; } = new();

    public PaymentProviderSettings For(string provider) =>
        provider == "MOMO" ? Momo : ZaloPay;
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
