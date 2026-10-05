namespace StreetBiz.Infrastructure.Security;

public sealed class OtpSettings
{
    public const string SectionName = "Otp";

    /// <summary>Server secret for HMAC-hashing OTP codes. Set via user-secrets / env var.</summary>
    public string HashKey { get; set; } = string.Empty;
}
