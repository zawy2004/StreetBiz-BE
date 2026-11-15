namespace StreetBiz.Infrastructure.Security;

public sealed class OtpSettings
{
    public const string SectionName = "Otp";

    /// <summary>Server secret for HMAC-hashing OTP codes. Set via user-secrets / env var.</summary>
    public string HashKey { get; set; } = string.Empty;

    /// <summary>
    /// Most OTP messages one client address may cause per hour, whatever the numbers (SMS-pumping guard).
    /// 0 turns it off. A shared NAT shares the count, so keep it above what one household or shop needs.
    /// </summary>
    public int MaxPerIpPerHour { get; set; } = 10;
}
