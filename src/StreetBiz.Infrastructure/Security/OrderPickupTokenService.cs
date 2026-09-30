using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.Infrastructure.Security;

public sealed class OrderPickupSettings
{
    public const string SectionName = "OrderPickup";

    /// <summary>HMAC-SHA256 key for signing order pickup QR payloads. Set outside source control.</summary>
    public string SigningKey { get; init; } = string.Empty;
}

/// <summary>
/// Token shape: "SBO1.&lt;base64url(order_id[8] | customer_user_id[8])&gt;.&lt;base64url(hmac[16])&gt;",
/// short enough for a dense QR and carrying no personal data - only two ids that
/// mean nothing without the database behind them.
///
/// The payload has no nonce on purpose: the code must stay the same every time
/// the customer reopens their order. Replay is not what stops a second use -
/// the order leaving READY_FOR_PICKUP is, and that check lives in the one
/// transaction that completes an order.
/// </summary>
public sealed class OrderPickupTokenService(IOptions<OrderPickupSettings> options)
    : IOrderPickupTokenService
{
    private const string Prefix = "SBO1";
    private const int PayloadLength = 16; // 8 (orderId) + 8 (customerUserId)
    private const int SignatureLength = 16; // truncated HMAC-SHA256

    private readonly byte[] key = Encoding.UTF8.GetBytes(options.Value.SigningKey);

    public string Create(long orderId, long customerUserId)
    {
        var payload = new byte[PayloadLength];
        BitConverter.GetBytes(orderId).CopyTo(payload, 0);
        BitConverter.GetBytes(customerUserId).CopyTo(payload, 8);

        return $"{Prefix}.{Base64UrlEncode(payload)}.{Base64UrlEncode(ComputeSignature(payload))}";
    }

    public bool TryParse(string token, out OrderPickupTokenClaims claims)
    {
        claims = null!;

        var parts = (token ?? string.Empty).Trim().Split('.');
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

        claims = new OrderPickupTokenClaims(
            BitConverter.ToInt64(payload, 0),
            BitConverter.ToInt64(payload, 8));
        return true;
    }

    // Crockford's base32: no I, L, O or U, so nothing in a printed code can be
    // mistaken for 1, 0 or a swear word read out across a busy stall.
    private const string ShortCodeAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int ShortCodeLength = 8; // 40 bits

    public string CreateShortCode(long orderId, long customerUserId)
    {
        var payload = new byte[PayloadLength + 1];
        BitConverter.GetBytes(orderId).CopyTo(payload, 0);
        BitConverter.GetBytes(customerUserId).CopyTo(payload, 8);
        // Domain separator: the short code must never be derivable from, or
        // collide with, the QR payload signed above.
        payload[PayloadLength] = 1;

        var digest = HMACSHA256.HashData(key, payload);
        return string.Create(ShortCodeLength, digest, (span, source) =>
        {
            for (var i = 0; i < ShortCodeLength; i++)
            {
                span[i] = ShortCodeAlphabet[source[i] % ShortCodeAlphabet.Length];
            }
        });
    }

    public string? NormaliseShortCode(string? typed)
    {
        if (string.IsNullOrWhiteSpace(typed)) return null;

        var cleaned = new char[ShortCodeLength];
        var length = 0;
        foreach (var character in typed.ToUpperInvariant())
        {
            // Accept what people actually type for the letters Crockford drops.
            var mapped = character switch
            {
                'I' or 'L' => '1',
                'O' => '0',
                'U' => 'V',
                _ => character,
            };
            if (ShortCodeAlphabet.IndexOf(mapped) < 0) continue; // spaces, dashes
            if (length == ShortCodeLength) return null; // too long to be a code
            cleaned[length++] = mapped;
        }

        return length == ShortCodeLength ? new string(cleaned) : null;
    }

    public bool ShortCodeMatches(string normalisedCode, long orderId, long customerUserId) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(normalisedCode),
            Encoding.ASCII.GetBytes(CreateShortCode(orderId, customerUserId)));

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
