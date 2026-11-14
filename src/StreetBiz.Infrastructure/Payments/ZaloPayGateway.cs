using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;

namespace StreetBiz.Infrastructure.Payments;

/// <summary>
/// ZaloPay Gateway v2 (https://docs.zalopay.vn): create an order, verify the callback, query an order.
/// The request MAC is HMAC-SHA256 with <c>key1</c> over the fields joined with "|" in the order the
/// documentation gives; the callback's MAC is HMAC-SHA256 with <c>key2</c> over the <c>data</c> string
/// exactly as received. Hex output is lower-case.
///
/// NOT YET TESTED AGAINST ZALOPAY'S REAL SANDBOX. It was written from the public documentation and is
/// covered by unit tests against vectors this repository computes itself; it stays switched off
/// (<see cref="PaymentProviderSettings.IsZaloPayConfigured"/>) until sandbox credentials are supplied.
/// </summary>
internal static class ZaloPayGateway
{
    public const string HttpClientName = "zalopay";

    private const string CreatePath = "create";
    private const string QueryPath = "query";

    /// <summary>The user id every ZaloPay order is filed under: StreetBiz has one merchant account, not a ZaloPay user per buyer.</summary>
    private const string AppUser = "StreetBiz";

    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    /// <summary>
    /// ZaloPay wants "yyMMdd_" (the day in Vietnam time) followed by anything unique within that day for
    /// the merchant. A fresh value on every create, so a retried checkout of the same payment never
    /// reuses an order id ZaloPay has already seen; the transaction id stays in it so a payment can be
    /// traced by eye.
    /// </summary>
    public static string NewAppTransId(long transactionId, DateTimeOffset now) =>
        now.ToOffset(VietnamOffset).ToString("yyMMdd", CultureInfo.InvariantCulture)
        + "_SB" + transactionId.ToString(CultureInfo.InvariantCulture)
        + Guid.NewGuid().ToString("N")[..8];

    /// <summary>The merchant data ZaloPay hands back unchanged in the callback: our idempotency key, and where the buyer returns to.</summary>
    public static string EmbedData(string idempotencyKey, string redirectUrl) =>
        JsonSerializer.Serialize(new { redirecturl = redirectUrl, idempotencyKey });

    public static string? IdempotencyKeyFrom(string? embedData)
    {
        if (string.IsNullOrWhiteSpace(embedData))
        {
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(embedData);
            return Str(json.RootElement, "idempotencyKey");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string Mac(string key, string data)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(data))).ToLowerInvariant();
    }

    public static async Task<PaymentGatewayCheckoutResult> CreateAsync(
        HttpClient http,
        PaymentProviderSettings zalo,
        PaymentGatewayCheckoutRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var appTransId = NewAppTransId(request.TransactionId, now);
        // VND has no subunit; ZaloPay takes an integer amount.
        var amount = decimal.ToInt64(decimal.Round(request.Amount, 0));
        var amountText = amount.ToString(CultureInfo.InvariantCulture);
        var appTime = now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var redirectUrl = zalo.ReturnUrlFor(request.Purpose)
            .Replace("{referenceId}", request.ReferenceId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{transactionId}", request.TransactionId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        var embedData = EmbedData(request.IdempotencyKey, redirectUrl);
        var item = JsonSerializer.Serialize(new[]
        {
            new
            {
                itemid = request.ReferenceCode,
                itemname = $"StreetBiz {request.ReferenceCode}",
                itemprice = amount,
                itemquantity = 1,
            },
        });
        var mac = Mac(zalo.Key1!, $"{zalo.AppId}|{appTransId}|{AppUser}|{amountText}|{appTime}|{embedData}|{item}");

        using var response = await http.PostAsync(
            Endpoint(zalo, CreatePath),
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["app_id"] = zalo.AppId!,
                ["app_user"] = AppUser,
                ["app_time"] = appTime,
                ["amount"] = amountText,
                ["app_trans_id"] = appTransId,
                ["embed_data"] = embedData,
                ["item"] = item,
                ["description"] = $"StreetBiz - thanh toán {request.ReferenceCode}",
                ["bank_code"] = "",
                ["callback_url"] = zalo.CallbackUrl!,
                ["mac"] = mac,
            }),
            cancellationToken);
        using var json = await ReadJsonAsync(response, cancellationToken);
        var root = json.RootElement;
        var returnCode = Int(root, "return_code");
        var orderUrl = Str(root, "order_url");
        if (returnCode != 1 || string.IsNullOrWhiteSpace(orderUrl))
        {
            throw new DomainRuleException(
                $"ZaloPay từ chối tạo giao dịch: {Str(root, "return_message") ?? "không rõ lý do"} (mã {returnCode}).");
        }

        return new PaymentGatewayCheckoutResult(orderUrl, appTransId);
    }

    /// <summary>
    /// Asks ZaloPay for an order's current state. Like MoMo's query, the answer comes over TLS in reply to a
    /// request signed with our key, so it is trusted as a verified callback would be.
    /// </summary>
    public static async Task<PaymentGatewayCallback> QueryAsync(
        HttpClient http,
        PaymentProviderSettings zalo,
        string appTransId,
        CancellationToken cancellationToken)
    {
        var mac = Mac(zalo.Key1!, $"{zalo.AppId}|{appTransId}|{zalo.Key1}");
        using var response = await http.PostAsync(
            Endpoint(zalo, QueryPath),
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["app_id"] = zalo.AppId!,
                ["app_trans_id"] = appTransId,
                ["mac"] = mac,
            }),
            cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        using var json = Parse(raw);
        var root = json.RootElement;
        var returnCode = Int(root, "return_code");
        return new PaymentGatewayCallback(
            appTransId,
            null,
            Dec(root, "amount"),
            Status(returnCode),
            SignatureValid: response.IsSuccessStatusCode && returnCode is not null,
            RawPayload: raw);
    }

    /// <summary>
    /// 1 = paid, 2 = failed. 3 (and anything else) means ZaloPay is still working on it, which is not a
    /// verdict: cancelling on it could void an order the buyer has in fact paid, so it is checked again later.
    /// </summary>
    public static string? Status(int? returnCode) => returnCode switch
    {
        1 => "SUCCESS",
        2 => "FAILED",
        _ => null,
    };

    /// <summary>True when the body looks like a ZaloPay callback ({"data": "...", "mac": "..."}) rather than the generic format.</summary>
    public static bool IsCallback(string rawPayload)
    {
        try
        {
            using var json = JsonDocument.Parse(rawPayload);
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String
                && json.RootElement.TryGetProperty("mac", out var mac) && mac.ValueKind == JsonValueKind.String;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// The callback is signed with key2 over the <c>data</c> string as sent. Only a callback whose MAC matches,
    /// for our own app id, counts; ZaloPay calls this URL only for a successful payment.
    /// </summary>
    public static PaymentGatewayCallback VerifyCallback(PaymentProviderSettings zalo, string rawPayload)
    {
        try
        {
            using var outer = JsonDocument.Parse(rawPayload);
            var data = Str(outer.RootElement, "data") ?? "";
            var supplied = Str(outer.RootElement, "mac") ?? "";
            var macValid = CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(Mac(zalo.Key2!, data)),
                Encoding.ASCII.GetBytes(supplied.ToLowerInvariant()));

            using var inner = JsonDocument.Parse(data);
            var root = inner.RootElement;
            var ours = string.Equals(Raw(root, "app_id"), zalo.AppId, StringComparison.Ordinal);
            return new PaymentGatewayCallback(
                Str(root, "app_trans_id"),
                IdempotencyKeyFrom(Str(root, "embed_data")),
                Dec(root, "amount"),
                "SUCCESS",
                macValid && ours,
                rawPayload);
        }
        catch (JsonException)
        {
            return new PaymentGatewayCallback(null, null, null, null, false, rawPayload);
        }
    }

    private static string Endpoint(PaymentProviderSettings zalo, string action) =>
        $"{zalo.ApiBaseUrl!.TrimEnd('/')}/{action}";

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var raw = await response.Content.ReadAsStringAsync(ct);
        try
        {
            return Parse(raw);
        }
        catch (JsonException)
        {
            throw new DomainRuleException($"ZaloPay trả về phản hồi không hợp lệ (HTTP {(int)response.StatusCode}).");
        }
    }

    private static JsonDocument Parse(string raw) => JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw);

    private static string? Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static decimal? Dec(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : null;

    /// <summary>A field as text whether ZaloPay sent it as a number or a string (app_id is a number in the callback).</summary>
    private static string Raw(JsonElement root, string name) =>
        !root.TryGetProperty(name, out var v) ? ""
        : v.ValueKind == JsonValueKind.String ? v.GetString() ?? ""
        : v.ValueKind is JsonValueKind.Null ? ""
        : v.GetRawText();
}
