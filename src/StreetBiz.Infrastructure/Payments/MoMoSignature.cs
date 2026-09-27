using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StreetBiz.Infrastructure.Payments;

/// <summary>
/// MoMo's extraData is a base64-encoded JSON object that it signs and echoes back unchanged in
/// the IPN. It carries our Idempotency-Key so an IPN can be matched to its transaction even when
/// its orderId belongs to an earlier checkout attempt.
/// </summary>
public static class MoMoExtraData
{
    public static string Encode(string idempotencyKey) =>
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new { idempotencyKey }));

    public static string? DecodeIdempotencyKey(string? extraData)
    {
        if (string.IsNullOrWhiteSpace(extraData))
        {
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(Convert.FromBase64String(extraData));
            return json.RootElement.TryGetProperty("idempotencyKey", out var key)
                && key.ValueKind == JsonValueKind.String
                    ? key.GetString()
                    : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// MoMo's AIO (all-in-one) v2 "captureWallet" signing scheme. Field order matters — the
/// rawSignature is the exact key=value pairs below joined by '&amp;', alphabetical by key,
/// signed with HMAC-SHA256 and the merchant's secretKey. Two different field sets: one for
/// opening a payment (create), a different one for the callback MoMo sends after the payer
/// finishes (IPN). Kept pure/static so both directions can be unit-tested without an HTTP call.
/// </summary>
public static class MoMoSignature
{
    public static string ForCreate(
        string accessKey,
        string amount,
        string extraData,
        string ipnUrl,
        string orderId,
        string orderInfo,
        string partnerCode,
        string redirectUrl,
        string requestId,
        string requestType) =>
        $"accessKey={accessKey}&amount={amount}&extraData={extraData}&ipnUrl={ipnUrl}" +
        $"&orderId={orderId}&orderInfo={orderInfo}&partnerCode={partnerCode}" +
        $"&redirectUrl={redirectUrl}&requestId={requestId}&requestType={requestType}";

    public static string ForIpn(
        string accessKey,
        string amount,
        string extraData,
        string message,
        string orderId,
        string orderInfo,
        string orderType,
        string partnerCode,
        string payType,
        string requestId,
        string responseTime,
        string resultCode,
        string transId) =>
        $"accessKey={accessKey}&amount={amount}&extraData={extraData}&message={message}" +
        $"&orderId={orderId}&orderInfo={orderInfo}&orderType={orderType}&partnerCode={partnerCode}" +
        $"&payType={payType}&requestId={requestId}&responseTime={responseTime}" +
        $"&resultCode={resultCode}&transId={transId}";

    public static string Sign(string rawSignature, string secretKey)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secretKey));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(rawSignature));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Constant-time compare, same discipline as the generic HMAC check elsewhere.</summary>
    public static bool Matches(string rawSignature, string secretKey, string? suppliedHex)
    {
        if (string.IsNullOrWhiteSpace(suppliedHex))
        {
            return false;
        }

        byte[] supplied;
        try
        {
            supplied = Convert.FromHexString(suppliedHex);
        }
        catch (FormatException)
        {
            return false;
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secretKey));
        var expected = hmac.ComputeHash(Encoding.UTF8.GetBytes(rawSignature));
        return supplied.Length == expected.Length && CryptographicOperations.FixedTimeEquals(supplied, expected);
    }
}
