using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Infrastructure.Payments;

namespace StreetBiz.Infrastructure.Security;

/// <summary>
/// Encrypts a payee's payOS keys before they are stored (AES-256-GCM, a fresh 12-byte nonce per
/// value, the 16-byte tag checked on the way back). Stored form: <c>v1:</c> + base64(nonce ‖
/// ciphertext ‖ tag). The key is <c>Payments:PayOs:CredentialKey</c> (base64 of 32 bytes); in
/// Development and Testing a fixed development key stands in when none is set, so the demo runs
/// out of the box. Elsewhere there is no fallback: <see cref="IsConfigured"/> is false and
/// nothing can be protected, which keeps PAYOS switched off rather than storing keys weakly.
/// </summary>
public sealed class AesGcmCredentialProtector : ICredentialProtector
{
    private const string Version = "v1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private static readonly byte[] DevelopmentKey =
        SHA256.HashData(Encoding.UTF8.GetBytes("streetbiz-development-only-payos-credential-key"));

    private readonly byte[]? key;

    public AesGcmCredentialProtector(IOptions<PaymentGatewaySettings> options, IHostEnvironment environment)
        : this(ResolveKey(options.Value.PayOs.CredentialKey, environment))
    {
    }

    /// <summary>Tests and tools: protect with exactly this 32-byte key (null: nothing can be protected).</summary>
    public AesGcmCredentialProtector(byte[]? key)
    {
        if (key is not null && key.Length != 32)
        {
            throw new ArgumentException("The credential key must be 32 bytes.", nameof(key));
        }

        this.key = key;
    }

    public bool IsConfigured => key is not null;

    public string Protect(string plaintext)
    {
        var activeKey = RequireKey();
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];
        using (var aes = new AesGcm(activeKey, TagSize))
        {
            aes.Encrypt(nonce, plain, cipher, tag);
        }

        var packed = new byte[NonceSize + cipher.Length + TagSize];
        nonce.CopyTo(packed, 0);
        cipher.CopyTo(packed, NonceSize);
        tag.CopyTo(packed, NonceSize + cipher.Length);
        return Version + Convert.ToBase64String(packed);
    }

    public string Unprotect(string protectedValue)
    {
        var activeKey = RequireKey();
        if (!protectedValue.StartsWith(Version, StringComparison.Ordinal))
        {
            throw new CryptographicException("Unknown protected value format.");
        }

        byte[] packed;
        try
        {
            packed = Convert.FromBase64String(protectedValue[Version.Length..]);
        }
        catch (FormatException exception)
        {
            throw new CryptographicException("The protected value is not valid base64.", exception);
        }

        if (packed.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("The protected value is too short.");
        }

        var nonce = packed.AsSpan(0, NonceSize);
        var cipher = packed.AsSpan(NonceSize, packed.Length - NonceSize - TagSize);
        var tag = packed.AsSpan(packed.Length - TagSize, TagSize);
        var plain = new byte[cipher.Length];
        using (var aes = new AesGcm(activeKey, TagSize))
        {
            // Throws AuthenticationTagMismatchException (a CryptographicException) when tampered
            // with or encrypted under another key.
            aes.Decrypt(nonce, cipher, tag, plain);
        }

        return Encoding.UTF8.GetString(plain);
    }

    private byte[] RequireKey() =>
        key ?? throw new InvalidOperationException(
            "Payments:PayOs:CredentialKey is not set (base64 of 32 random bytes, in user-secrets or App Settings).");

    private static byte[]? ResolveKey(string? configured, IHostEnvironment environment)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            byte[] decoded;
            try
            {
                decoded = Convert.FromBase64String(configured.Trim());
            }
            catch (FormatException exception)
            {
                throw new InvalidOperationException(
                    "Payments:PayOs:CredentialKey must be base64 of 32 random bytes.", exception);
            }

            return decoded.Length == 32
                ? decoded
                : throw new InvalidOperationException("Payments:PayOs:CredentialKey must decode to exactly 32 bytes.");
        }

        return environment.IsDevelopment() || environment.IsEnvironment("Testing") ? DevelopmentKey : null;
    }
}
