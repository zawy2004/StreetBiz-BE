using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.Infrastructure.Security;

public sealed class PermitSettings
{
    public const string SectionName = "Permit";

    /// <summary>HMAC-SHA256 key for signing permit QR payloads. Must be set outside source control.</summary>
    public string SigningKey { get; init; } = string.Empty;
}

/// <summary>
/// Token shape: "SBP1.&lt;base64url(contract_id[8] | issued_at_ticks[8] | random[8])&gt;.&lt;base64url(hmac[16])&gt;",
/// roughly 60 characters -- short enough for a dense QR code, with no PII and no fixed
/// dependency on the IDENTITY-generated permit_id.
/// </summary>
public sealed class PermitTokenService(IOptions<PermitSettings> options) : IPermitTokenService
{
    private const string Prefix = "SBP1";
    private const int PayloadLength = 24; // 8 (contractId) + 8 (issuedAt ticks) + 8 (random)
    private const int SignatureLength = 16; // truncated HMAC-SHA256

    private readonly byte[] key = Encoding.UTF8.GetBytes(options.Value.SigningKey);

    public string Create(long contractId, DateTime issuedAtUtc)
    {
        var payload = new byte[PayloadLength];
        BitConverter.GetBytes(contractId).CopyTo(payload, 0);
        BitConverter.GetBytes(issuedAtUtc.Ticks).CopyTo(payload, 8);
        RandomNumberGenerator.Fill(payload.AsSpan(16, 8));

        var signature = ComputeSignature(payload);
        return $"{Prefix}.{Base64UrlEncode(payload)}.{Base64UrlEncode(signature)}";
    }

    public bool TryParse(string token, out PermitTokenClaims claims)
    {
        claims = null!;

        var parts = token.Split('.');
        if (parts.Length != 3 || parts[0] != Prefix)
        {
            return false;
        }

        byte[] payload;
        byte[] signature;
        try
        {
            payload = Base64UrlDecode(parts[1]);
            signature = Base64UrlDecode(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (payload.Length != PayloadLength || signature.Length != SignatureLength)
        {
            return false;
        }

        if (!CryptographicOperations.FixedTimeEquals(signature, ComputeSignature(payload)))
        {
            return false;
        }

        var contractId = BitConverter.ToInt64(payload, 0);
        var issuedAtTicks = BitConverter.ToInt64(payload, 8);
        claims = new PermitTokenClaims(contractId, new DateTime(issuedAtTicks, DateTimeKind.Utc));
        return true;
    }

    private byte[] ComputeSignature(byte[] payload) =>
        HMACSHA256.HashData(key, payload)[..SignatureLength];

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            _ => string.Empty,
        };
        return Convert.FromBase64String(padded);
    }
}
