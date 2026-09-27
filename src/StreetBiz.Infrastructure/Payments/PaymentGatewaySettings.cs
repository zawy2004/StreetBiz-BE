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

    // Real gateway credentials. Currently only wired up for MoMo's "captureWallet" AIO v2 API
    // (see ConfiguredPaymentGateway); ZaloPay still uses CheckoutUrlTemplate/CallbackSecret above.
    public string? PartnerCode { get; set; }
    public string? AccessKey { get; set; }
    public string? SecretKey { get; set; }

    /// <summary>The create-payment endpoint, e.g. https://test-payment.momo.vn/v2/gateway/api/create.</summary>
    public string? ApiEndpoint { get; set; }

    /// <summary>Where MoMo sends the payer's browser back to after paying (cosmetic only — never trusted for confirmation).</summary>
    public string? RedirectUrl { get; set; }

    /// <summary>Where MoMo POSTs the IPN. Must be publicly reachable — MoMo's servers call this, not the payer's browser.</summary>
    public string? IpnUrl { get; set; }

    public bool HasRealCredentials =>
        !string.IsNullOrWhiteSpace(PartnerCode)
        && !string.IsNullOrWhiteSpace(AccessKey)
        && !string.IsNullOrWhiteSpace(SecretKey)
        && !string.IsNullOrWhiteSpace(ApiEndpoint);
}
