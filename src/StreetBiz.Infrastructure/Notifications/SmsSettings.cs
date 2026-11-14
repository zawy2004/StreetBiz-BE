namespace StreetBiz.Infrastructure.Notifications;

/// <summary>
/// SMS delivery configuration. Outside Development a provider is mandatory;
/// credentials come from user-secrets / env vars, never from a checked-in appsettings file.
/// </summary>
public sealed class SmsSettings
{
    public const string SectionName = "Sms";

    /// <summary>"Logging" (development only), "Http" (generic JSON gateway) or "Esms" (eSMS.vn).</summary>
    public string Provider { get; set; } = "Logging";

    /// <summary>Http: provider endpoint that accepts a JSON POST of {to, message}. Esms: optional override of eSMS's URL.</summary>
    public string Endpoint { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public string Sender { get; set; } = "StreetBiz";

    /// <summary>Esms only.</summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>Esms only: "8" = fixed number (no brandname), "2" = customer care (needs a registered brandname).</summary>
    public string SmsType { get; set; } = "8";

    /// <summary>Esms only: required when <see cref="SmsType"/> is 2.</summary>
    public string Brandname { get; set; } = string.Empty;

    /// <summary>Esms only: true asks eSMS to validate the request without sending or charging.</summary>
    public bool Sandbox { get; set; }
}
