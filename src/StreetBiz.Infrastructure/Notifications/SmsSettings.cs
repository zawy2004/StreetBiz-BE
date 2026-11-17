namespace StreetBiz.Infrastructure.Notifications;

/// <summary>
/// SMS delivery configuration. Outside Development a provider is mandatory;
/// credentials come from user-secrets / env vars, never from a checked-in appsettings file.
/// </summary>
public sealed class SmsSettings
{
    public const string SectionName = "Sms";

    /// <summary>"Logging" (development only), "Http" (generic JSON gateway) or "SpeedSms" (SpeedSMS.vn).</summary>
    public string Provider { get; set; } = "Logging";

    /// <summary>Http: provider endpoint that accepts a JSON POST of {to, message}. SpeedSms: optional override of SpeedSMS's URL.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Http: bearer key. SpeedSms: the API access token (connect.speedsms.vn -> Tai khoan).</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Http: "from". SpeedSms: the registered brandname (type 3) or Android deviceId (type 5); unused otherwise.</summary>
    public string Sender { get; set; } = "StreetBiz";

    /// <summary>
    /// SpeedSms only: "2" customer care (random number, no registration), "3" brandname (needs <see cref="Sender"/>),
    /// "4" default brandname Notify, "5" own Android phone (needs <see cref="Sender"/>), "6" fixed number.
    /// </summary>
    public string SmsType { get; set; } = "2";
}
