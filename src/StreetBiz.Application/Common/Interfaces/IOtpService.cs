namespace StreetBiz.Application.Common.Interfaces;

/// <summary>Issues and verifies phone one-time passwords (BR-03, BR-61, SEC-09).</summary>
public interface IOtpService
{
    /// <summary>Generate, store (hashed) and deliver an OTP. Enforces resend cooldown.</summary>
    Task IssueAsync(string phoneNumber, string purpose, CancellationToken cancellationToken);

    /// <summary>Validate and consume the active challenge. Throws on wrong/expired/locked code.</summary>
    Task ConsumeAsync(string phoneNumber, string purpose, string code, CancellationToken cancellationToken);
}
