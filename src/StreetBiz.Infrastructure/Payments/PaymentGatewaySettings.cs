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
}
