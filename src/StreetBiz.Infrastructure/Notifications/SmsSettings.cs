namespace StreetBiz.Infrastructure.Notifications;

/// <summary>
/// SMS delivery configuration. Outside Development a provider endpoint is mandatory;
/// credentials come from user-secrets / env vars, never from a checked-in appsettings file.
/// </summary>
public sealed class SmsSettings
{
    public const string SectionName = "Sms";

    /// <summary>"Logging" (development only) or "Http".</summary>
    public string Provider { get; set; } = "Logging";

    /// <summary>Provider endpoint that accepts a JSON POST of {to, message}.</summary>
    public string Endpoint { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public string Sender { get; set; } = "StreetBiz";
}
