using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;

namespace StreetBiz.Infrastructure.Payments;

/// <summary>
/// MoMo All-In-One v2: create a wallet checkout, query its status, verify the IPN.
/// Every request and the IPN are signed with HMAC-SHA256 over "key=value&amp;…" in the
/// exact field order MoMo documents; a wrong order is a wrong signature.
/// </summary>
internal static class MomoGateway
{
    public const string HttpClientName = "momo";

    /// <summary>
    /// A fresh MoMo orderId for every create call: "SB-T{transactionId}-{8 hex}". MoMo refuses
    /// any orderId it has seen before (resultCode 41) — including a retry of the same
    /// still-pending order — and orderIds are unique per partner code, which everyone using
    /// MoMo's shared test key shares, and which restarts from SB-T1 whenever a database is
    /// re-seeded. A bare "SB-T{transactionId}" was therefore refused on the very first attempt
    /// once anyone had tested with that transaction number. The transaction id stays in front
    /// so the return page can still tell which payment it is. MoMo allows [0-9a-zA-Z-_.].
    /// </summary>
    public static string NewOrderId(long transactionId) =>
        "SB-T" + transactionId.ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// extraData is base64 JSON that MoMo signs and echoes back unchanged in the IPN. It carries
    /// our Idempotency-Key, so an IPN for an earlier attempt of the same transaction (whose
    /// orderId a retry has since replaced in provider_reference) still matches it.
    /// </summary>
    public static string ExtraData(string idempotencyKey) =>
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new { idempotencyKey }));

    public static string? IdempotencyKeyFrom(string? extraData)
    {
        if (string.IsNullOrWhiteSpace(extraData))
        {
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(Convert.FromBase64String(extraData));
            return Str(json.RootElement, "idempotencyKey");
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    public static async Task<PaymentGatewayCheckoutResult> CreateAsync(
        HttpClient http,
        PaymentProviderSettings momo,
        PaymentGatewayCheckoutRequest request,
        CancellationToken cancellationToken)
    {
        var orderId = NewOrderId(request.TransactionId);
        var requestId = Guid.NewGuid().ToString("N"); // unique per call, MoMo max 50 chars
        // VND has no subunit; MoMo takes an integer amount (min 1,000, max 50,000,000).
        var amount = decimal.ToInt64(decimal.Round(request.Amount, 0)).ToString(CultureInfo.InvariantCulture);
        var orderInfo = $"StreetBiz {request.ReferenceCode}";
        var redirectUrl = momo.ReturnUrlFor(request.Purpose)
            .Replace("{referenceId}", request.ReferenceId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{transactionId}", request.TransactionId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        var ipnUrl = momo.NotifyUrl!;
        var extraData = ExtraData(request.IdempotencyKey);

        var signature = Sign(momo.SecretKey!,
            $"accessKey={momo.AccessKey}&amount={amount}&extraData={extraData}&ipnUrl={ipnUrl}" +
            $"&orderId={orderId}&orderInfo={orderInfo}&partnerCode={momo.PartnerCode}" +
            $"&redirectUrl={redirectUrl}&requestId={requestId}&requestType={momo.RequestType}");

        using var response = await http.PostAsJsonAsync(
            Endpoint(momo, "create"),
            new
            {
                partnerCode = momo.PartnerCode,
                accessKey = momo.AccessKey,
                requestId,
                amount,
                orderId,
                orderInfo,
                redirectUrl,
                ipnUrl,
                extraData,
                requestType = momo.RequestType,
                signature,
                lang = "vi",
            },
            cancellationToken);
        using var json = await ReadJsonAsync(response, cancellationToken);
        var root = json.RootElement;
        var resultCode = Int(root, "resultCode");
        var payUrl = Str(root, "payUrl");
        if (resultCode != 0 || string.IsNullOrWhiteSpace(payUrl))
        {
            throw new DomainRuleException(
                $"MoMo từ chối tạo giao dịch: {Str(root, "message") ?? "không rõ lý do"} (mã {resultCode}).");
        }

        return new PaymentGatewayCheckoutResult(payUrl, orderId);
    }

    /// <summary>
    /// Asks MoMo for the transaction's current state. The answer comes over TLS from MoMo,
    /// signed by our own credentials on the request, so it is trusted like a verified IPN.
    /// </summary>
    public static async Task<PaymentGatewayCallback> QueryAsync(
        HttpClient http,
        PaymentProviderSettings momo,
        string orderId,
        CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var signature = Sign(momo.SecretKey!,
            $"accessKey={momo.AccessKey}&orderId={orderId}&partnerCode={momo.PartnerCode}&requestId={requestId}");
        using var response = await http.PostAsJsonAsync(
            Endpoint(momo, "query"),
            new { partnerCode = momo.PartnerCode, requestId, orderId, signature, lang = "vi" },
            cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        using var json = Parse(raw);
        var root = json.RootElement;
        return new PaymentGatewayCallback(
            Str(root, "orderId") ?? orderId,
            null,
            Dec(root, "amount"),
            Status(Int(root, "resultCode")),
            SignatureValid: response.IsSuccessStatusCode && Str(root, "partnerCode") == momo.PartnerCode,
            RawPayload: raw);
    }

    /// <summary>True when the payload looks like a MoMo IPN rather than the generic format.</summary>
    public static bool IsIpn(string rawPayload) =>
        rawPayload.Contains("\"partnerCode\"", StringComparison.Ordinal)
        && rawPayload.Contains("\"resultCode\"", StringComparison.Ordinal);

    public static PaymentGatewayCallback VerifyIpn(PaymentProviderSettings momo, string rawPayload)
    {
        try
        {
            using var json = Parse(rawPayload);
            var root = json.RootElement;
            var expected = Sign(momo.SecretKey!,
                $"accessKey={momo.AccessKey}&amount={Raw(root, "amount")}&extraData={Raw(root, "extraData")}" +
                $"&message={Raw(root, "message")}&orderId={Raw(root, "orderId")}&orderInfo={Raw(root, "orderInfo")}" +
                $"&orderType={Raw(root, "orderType")}&partnerCode={Raw(root, "partnerCode")}&payType={Raw(root, "payType")}" +
                $"&requestId={Raw(root, "requestId")}&responseTime={Raw(root, "responseTime")}" +
                $"&resultCode={Raw(root, "resultCode")}&transId={Raw(root, "transId")}");
            var supplied = Str(root, "signature") ?? "";
            var valid = Str(root, "partnerCode") == momo.PartnerCode
                && CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(supplied.ToLowerInvariant()));
            return new PaymentGatewayCallback(
                Str(root, "orderId"),
                IdempotencyKeyFrom(Str(root, "extraData")),
                Dec(root, "amount"),
                Status(Int(root, "resultCode")),
                valid,
                rawPayload);
        }
        catch (JsonException)
        {
            return new PaymentGatewayCallback(null, null, null, null, false, rawPayload);
        }
    }

    /// <summary>
    /// 0 = paid. Only MoMo's documented payment-failure codes count as FAILED (insufficient
    /// balance, declined, cancelled, over limit, expired, buyer refused, account blocked...).
    /// Everything else, including 1000/7000/9000 (still in flight) and request or system
    /// errors (11, 12, 13, 42, 99...), is undecided: cancelling on those could void an
    /// order the buyer has in fact paid, so it is simply checked again later.
    /// </summary>
    public static string? Status(int? resultCode) => resultCode switch
    {
        0 => "SUCCESS",
        1001 or 1002 or 1003 or 1004 or 1005 or 1006 or 1007 or 1026 or 4001 or 4100 => "FAILED",
        _ => null,
    };

    public static string Sign(string secretKey, string raw)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secretKey));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

    private static string Endpoint(PaymentProviderSettings momo, string action) =>
        $"{momo.ApiBaseUrl!.TrimEnd('/')}/{action}";

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var raw = await response.Content.ReadAsStringAsync(ct);
        try
        {
            return Parse(raw);
        }
        catch (JsonException)
        {
            throw new DomainRuleException($"MoMo trả về phản hồi không hợp lệ (HTTP {(int)response.StatusCode}).");
        }
    }

    private static JsonDocument Parse(string raw) => JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw);

    private static string? Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static decimal? Dec(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : null;

    /// <summary>A field exactly as MoMo sent it (numbers unquoted), for the signature string.</summary>
    private static string Raw(JsonElement root, string name) =>
        !root.TryGetProperty(name, out var v) ? ""
        : v.ValueKind == JsonValueKind.String ? v.GetString() ?? ""
        : v.ValueKind is JsonValueKind.Null ? ""
        : v.GetRawText();
}
