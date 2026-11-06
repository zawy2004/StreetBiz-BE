using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.Infrastructure.Payments;

/// <summary>
/// payOS merchant API v2 (https://payos.vn/docs/api/), always called with one payee's own channel
/// keys, so the payer's transfer lands in that payee's bank account:
/// <list type="bullet">
/// <item>POST /v2/payment-requests opens a payment link. The request is signed with HMAC-SHA256
/// (hex) over "amount=…&amp;cancelUrl=…&amp;description=…&amp;orderCode=…&amp;returnUrl=…".</item>
/// <item>The webhook signs its <c>data</c> object: keys sorted, "key=value" joined by "&amp;",
/// null and the strings "null"/"undefined" as empty, arrays as JSON with sorted keys.</item>
/// <item>GET /v2/payment-requests/{orderCode} reports the link's state.</item>
/// </list>
/// <c>orderCode</c> is <c>transactionId × 1000 + attempt</c>: unique for every link StreetBiz opens,
/// and it carries the transaction back. A code below 1000 cannot be ours (payOS's test webhook
/// when a URL is registered), so it is acknowledged and ignored.
/// </summary>
internal static class PayOsGateway
{
    public const string HttpClientName = "payos";

    /// <summary>Codes 1-999 are left for payOS's own test requests.</summary>
    public const int AttemptsPerTransaction = 999;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Common VietQR bank BINs, for showing the payer which bank the account is at.</summary>
    private static readonly IReadOnlyDictionary<string, string> BankNames = new Dictionary<string, string>
    {
        ["970403"] = "Sacombank",
        ["970405"] = "Agribank",
        ["970407"] = "Techcombank",
        ["970415"] = "VietinBank",
        ["970416"] = "ACB",
        ["970418"] = "BIDV",
        ["970422"] = "MB Bank",
        ["970423"] = "TPBank",
        ["970426"] = "MSB",
        ["970431"] = "Eximbank",
        ["970432"] = "VPBank",
        ["970436"] = "Vietcombank",
        ["970437"] = "HDBank",
        ["970440"] = "SeABank",
        ["970441"] = "VIB",
        ["970443"] = "SHB",
        ["970448"] = "OCB",
        ["970452"] = "KienlongBank",
    };

    public static long OrderCode(long transactionId, int attempt) =>
        checked(transactionId * 1000 + attempt);

    /// <summary>
    /// How a link is stored as provider_reference: "PAYOS-42001". The prefix keeps it apart from
    /// references of other kinds (an officer may type a bare receipt number such as 42001).
    /// </summary>
    public const string ReferencePrefix = "PAYOS-";

    public static string Reference(long orderCode) => ReferencePrefix + orderCode.ToString(CultureInfo.InvariantCulture);

    /// <summary>The orderCode inside a stored reference ("PAYOS-42001" or a bare "42001").</summary>
    public static string OrderCodeOf(string providerReference) =>
        providerReference.StartsWith(ReferencePrefix, StringComparison.Ordinal)
            ? providerReference[ReferencePrefix.Length..]
            : providerReference;

    /// <summary>The transaction a link code belongs to; null for codes that cannot be ours.</summary>
    public static long? TransactionIdOf(long orderCode) => orderCode >= 1000 ? orderCode / 1000 : null;

    public static string BankName(string? bin) =>
        bin is not null && BankNames.TryGetValue(bin, out var name) ? name
        : string.IsNullOrWhiteSpace(bin) ? "Ngân hàng" : $"Ngân hàng (mã {bin})";

    // ------------------------------------------------------------------------------ checkout

    /// <summary>What StreetBiz keeps of a link, so the same QR is shown again and the next attempt gets a new code.</summary>
    internal sealed record StoredCheckout(
        long OrderCode,
        int Attempt,
        long PayeeAccountId,
        string? PaymentLinkId,
        string CheckoutUrl,
        string QrCode,
        string? Bin,
        string? AccountNumber,
        string? AccountName,
        string Description,
        long Amount,
        DateTimeOffset ExpiresAt);

    public static async Task<PaymentGatewayCheckoutResult> CreateAsync(
        HttpClient http,
        PayOsSettings settings,
        PaymentGatewayCheckoutRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var payee = request.Payee
            ?? throw new InvalidOperationException("A payOS checkout needs the payee's channel.");
        var attempt = (ReadStored(request.PreviousCheckoutPayload)?.Attempt ?? 0) + 1;
        if (attempt > AttemptsPerTransaction)
        {
            throw new PayeeChannelException("Khoản này đã tạo mã thanh toán quá nhiều lần. Hãy tạo khoản thanh toán mới.");
        }

        var orderCode = OrderCode(request.TransactionId, attempt);
        var amount = (long)decimal.Round(request.Amount, 0, MidpointRounding.AwayFromZero);
        // payOS limits the content to 9 characters for accounts it does not link itself; the code
        // that matters is orderCode, so this is only what the payer's bank shows.
        var description = Shorten($"SB{request.TransactionId.ToString(CultureInfo.InvariantCulture)}");
        var returnUrl = ReturnUrl(settings, request);
        var expiresAt = now.AddMinutes(Math.Max(1, settings.LinkLifetimeMinutes));
        var signature = Sign(
            payee.ChecksumKey,
            $"amount={amount}&cancelUrl={returnUrl}&description={description}&orderCode={orderCode}&returnUrl={returnUrl}");

        using var message = new HttpRequestMessage(HttpMethod.Post, $"{settings.ApiBaseUrl.TrimEnd('/')}/v2/payment-requests")
        {
            Content = JsonContent(new
            {
                orderCode,
                amount,
                description,
                cancelUrl = returnUrl,
                returnUrl,
                expiredAt = (int)expiresAt.ToUnixTimeSeconds(),
                signature,
            }),
        };
        Authenticate(message, payee);
        using var response = await http.SendAsync(message, cancellationToken);
        var data = await ReadDataAsync(response, "tạo mã thanh toán", cancellationToken);

        var stored = new StoredCheckout(
            orderCode,
            attempt,
            payee.PayeeAccountId,
            Str(data, "paymentLinkId"),
            Str(data, "checkoutUrl") ?? "",
            Str(data, "qrCode") ?? "",
            Str(data, "bin"),
            Str(data, "accountNumber"),
            Str(data, "accountName"),
            Str(data, "description") ?? description,
            amount,
            expiresAt);
        if (string.IsNullOrWhiteSpace(stored.QrCode))
        {
            throw new PayeeChannelException("payOS không trả về mã QR. Vui lòng thử lại.");
        }

        return new PaymentGatewayCheckoutResult(
            stored.CheckoutUrl,
            Reference(orderCode),
            Instructions(stored))
        {
            CheckoutPayload = JsonSerializer.Serialize(stored, Json),
        };
    }

    /// <summary>The stored link's QR and details while it is still payable; null once it has expired.</summary>
    public static BankTransferInstructions? DescribeStored(string? checkoutPayload, DateTimeOffset now)
    {
        var stored = ReadStored(checkoutPayload);
        return stored is null || stored.ExpiresAt <= now ? null : Instructions(stored);
    }

    internal static StoredCheckout? ReadStored(string? checkoutPayload)
    {
        if (string.IsNullOrWhiteSpace(checkoutPayload))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<StoredCheckout>(checkoutPayload, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static BankTransferInstructions Instructions(StoredCheckout stored) =>
        new(BankName(stored.Bin), stored.AccountNumber ?? "", stored.AccountName, stored.Amount, stored.Description, "")
        {
            QrContent = stored.QrCode,
            CheckoutUrl = string.IsNullOrWhiteSpace(stored.CheckoutUrl) ? null : stored.CheckoutUrl,
        };

    private static string ReturnUrl(PayOsSettings settings, PaymentGatewayCheckoutRequest request)
    {
        var origin = settings.FrontendBaseUrl.TrimEnd('/');
        var id = request.ReferenceId.ToString(CultureInfo.InvariantCulture);
        return request.Purpose == PaymentPurposes.RentalFee
            ? $"{origin}/vendor/finance/fees/{id}/payment"
            : $"{origin}/customer/orders/{id}/payment";
    }

    private static string Shorten(string description) => description.Length <= 9 ? description : "StreetBiz";

    // ------------------------------------------------------------------------------- webhook

    /// <summary>
    /// A payOS webhook, checked with the checksum key of the channel its URL belongs to. Without
    /// that key (an unknown URL) nothing authenticates. Only a successful payment ("00") is a
    /// SUCCESS; anything else carries no status and is not applied.
    /// </summary>
    public static PaymentGatewayCallback VerifyWebhook(PayeeCredentials? payee, string rawPayload)
    {
        try
        {
            using var json = JsonDocument.Parse(rawPayload);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object)
            {
                return new PaymentGatewayCallback(null, null, null, null, false, rawPayload);
            }

            var supplied = Str(root, "signature");
            var authentic = payee is not null
                && !string.IsNullOrWhiteSpace(supplied)
                && HexEquals(Sign(payee.ChecksumKey, SignedText(data)), supplied);

            var orderCode = Long(data, "orderCode");
            var paid = Str(root, "code") == "00" && (Str(data, "code") ?? "00") == "00";
            return new PaymentGatewayCallback(
                orderCode is { } reference ? Reference(reference) : null,
                null,
                Dec(data, "amount"),
                paid ? PaymentStatuses.Success : null,
                authentic,
                rawPayload,
                Str(data, "reference"),
                Ignored: orderCode is not { } code || TransactionIdOf(code) is null);
        }
        catch (JsonException)
        {
            return new PaymentGatewayCallback(null, null, null, null, false, rawPayload);
        }
    }

    /// <summary>The text payOS signs: the object's keys sorted, "key=value" joined by "&amp;".</summary>
    public static string SignedText(JsonElement data)
    {
        var parts = new List<string>();
        foreach (var property in data.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            parts.Add($"{property.Name}={ValueText(property.Value)}");
        }

        return string.Join('&', parts);
    }

    private static string ValueText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        JsonValueKind.String => value.GetString() is "null" or "undefined" ? "" : value.GetString() ?? "",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Array => SortedJson(value),
        JsonValueKind.Object => SortedJson(value),
        _ => value.GetRawText(),
    };

    /// <summary>JSON with every object's keys sorted, as payOS serialises arrays inside signed data.</summary>
    private static string SortedJson(JsonElement value) =>
        Sorted(JsonNode.Parse(value.GetRawText()))?.ToJsonString() ?? "null";

    private static JsonNode? Sorted(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => KeyValuePair.Create(p.Key, Sorted(p.Value?.DeepClone())))),
        JsonArray array => new JsonArray(array.Select(item => Sorted(item?.DeepClone())).ToArray()),
        _ => node?.DeepClone(),
    };

    // --------------------------------------------------------------------------- status query

    /// <summary>
    /// The link's state. PAID is a SUCCESS for the link's amount; CANCELLED, EXPIRED and FAILED are
    /// FAILED; anything else (PENDING, PROCESSING, an under-payment) is not final yet. The answer
    /// comes over TLS from payOS with the payee's own keys, so it is trusted like a signed webhook.
    /// A busy payOS (429) or an outage reads as "not final": the next check asks again.
    /// </summary>
    public static async Task<PaymentGatewayCallback> QueryAsync(
        HttpClient http,
        PayOsSettings settings,
        PayeeCredentials payee,
        string providerReference,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(
            HttpMethod.Get,
            $"{settings.ApiBaseUrl.TrimEnd('/')}/v2/payment-requests/{Uri.EscapeDataString(OrderCodeOf(providerReference))}");
        Authenticate(message, payee);
        using var response = await http.SendAsync(message, cancellationToken);
        if (IsUnauthorized(response.StatusCode))
        {
            throw Refused();
        }

        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new PaymentGatewayCallback(providerReference, null, null, null, true, raw);
        }

        try
        {
            using var json = JsonDocument.Parse(raw);
            var root = json.RootElement;
            if (Str(root, "code") != "00"
                || !root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object)
            {
                return new PaymentGatewayCallback(providerReference, null, null, null, true, raw);
            }

            var status = (Str(data, "status") ?? "").ToUpperInvariant() switch
            {
                "PAID" => PaymentStatuses.Success,
                "CANCELLED" or "EXPIRED" or "FAILED" => PaymentStatuses.Failed,
                _ => null,
            };
            string? reference = null;
            if (data.TryGetProperty("transactions", out var transactions)
                && transactions.ValueKind == JsonValueKind.Array)
            {
                reference = transactions.EnumerateArray().Select(t => Str(t, "reference")).FirstOrDefault(r => r is not null);
            }

            return new PaymentGatewayCallback(
                providerReference,
                null,
                Dec(data, "amount"),
                status,
                true,
                data.GetRawText(),
                reference);
        }
        catch (JsonException)
        {
            return new PaymentGatewayCallback(providerReference, null, null, null, true, raw);
        }
    }

    // ------------------------------------------------------------------------- channel check

    /// <summary>
    /// Registers StreetBiz's webhook URL on the channel (payOS calls it once to test it) and reads
    /// back the bank account. Without a URL, only proves the keys are accepted.
    /// </summary>
    public static async Task<PayeeChannelInfo> VerifyChannelAsync(
        HttpClient http,
        PayOsSettings settings,
        PayeeCredentials payee,
        string? webhookUrl,
        CancellationToken cancellationToken)
    {
        var origin = settings.ApiBaseUrl.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            using var probe = new HttpRequestMessage(HttpMethod.Get, $"{origin}/v2/payment-requests/1");
            Authenticate(probe, payee);
            using var probed = await http.SendAsync(probe, cancellationToken);
            return IsUnauthorized(probed.StatusCode) ? throw Refused() : new PayeeChannelInfo(null, null, null, false);
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, $"{origin}/confirm-webhook")
        {
            Content = JsonContent(new { webhookUrl }),
        };
        Authenticate(message, payee);
        using var response = await http.SendAsync(message, cancellationToken);
        if (IsUnauthorized(response.StatusCode))
        {
            throw Refused();
        }

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            throw new PayeeChannelException(
                $"payOS không gọi được địa chỉ webhook {webhookUrl}. Kiểm tra Payments:PayOs:PublicWebhookBaseUrl " +
                "(địa chỉ https công khai của API, ví dụ đường hầm ngrok) rồi kết nối lại.");
        }

        var data = await ReadDataAsync(response, "xác nhận webhook", cancellationToken);
        var bank = Str(data, "shortName") ?? Str(data, "name");
        return new PayeeChannelInfo(bank, Str(data, "accountNumber"), Str(data, "accountName"), true);
    }

    // ------------------------------------------------------------------------------- helpers

    public static string Sign(string checksumKey, string text) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(checksumKey), Encoding.UTF8.GetBytes(text)))
            .ToLowerInvariant();

    private static bool HexEquals(string expected, string supplied) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected),
            Encoding.ASCII.GetBytes(supplied.Trim().ToLowerInvariant()));

    private static void Authenticate(HttpRequestMessage message, PayeeCredentials payee)
    {
        message.Headers.Add("x-client-id", payee.ClientId);
        message.Headers.Add("x-api-key", payee.ApiKey);
    }

    private static StringContent JsonContent(object body) =>
        new(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");

    private static bool IsUnauthorized(HttpStatusCode status) =>
        status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    private static PayeeChannelException Refused() =>
        new("payOS từ chối khoá của kênh nhận tiền (Client ID / API Key không đúng hoặc đã bị đổi). " +
            "Chủ tài khoản cần kết nối lại.", unauthorized: true);

    /// <summary>The <c>data</c> object of a successful ("00") payOS answer, as a detached element.</summary>
    private static async Task<JsonElement> ReadDataAsync(
        HttpResponseMessage response, string action, CancellationToken cancellationToken)
    {
        if (IsUnauthorized(response.StatusCode))
        {
            throw Refused();
        }

        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(raw).RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new PayeeChannelException($"payOS trả về phản hồi không hợp lệ khi {action} (HTTP {(int)response.StatusCode}).");
        }

        if (!response.IsSuccessStatusCode || Str(root, "code") != "00")
        {
            var reason = Str(root, "desc") ?? $"HTTP {(int)response.StatusCode}";
            throw new PayeeChannelException($"payOS từ chối {action}: {reason}");
        }

        return root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
            ? data
            : throw new PayeeChannelException($"payOS không trả về dữ liệu khi {action}.");
    }

    private static string? Str(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            }
            : null;

    private static long? Long(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number
            : value.ValueKind == JsonValueKind.String
                && long.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed
            : null
            : null;

    private static decimal? Dec(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)
            ? number
            : null;
}

/// <summary><see cref="IPayeeChannelGateway"/> on payOS.</summary>
public sealed class PayOsChannelGateway(
    Microsoft.Extensions.Options.IOptions<PaymentGatewaySettings> options,
    IHttpClientFactory httpClientFactory) : IPayeeChannelGateway
{
    private PayOsSettings Settings => options.Value.PayOs;

    public string? WebhookUrlFor(string webhookToken) =>
        string.IsNullOrWhiteSpace(Settings.PublicWebhookBaseUrl)
            ? null
            : $"{Settings.PublicWebhookBaseUrl.TrimEnd('/')}/api/payments/payos/callback/{Uri.EscapeDataString(webhookToken)}";

    public async Task<PayeeChannelInfo> VerifyChannelAsync(
        PayeeCredentials credentials, string? webhookUrl, CancellationToken cancellationToken)
    {
        if (!Settings.Enabled)
        {
            throw new PayeeChannelException("Thanh toán payOS đang tắt trên máy chủ (Payments:PayOs:Enabled).");
        }

        try
        {
            return await PayOsGateway.VerifyChannelAsync(
                httpClientFactory.CreateClient(PayOsGateway.HttpClientName), Settings, credentials, webhookUrl, cancellationToken);
        }
        catch (HttpRequestException)
        {
            throw new PayeeChannelException("Không kết nối được payOS. Vui lòng thử lại sau.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PayeeChannelException("payOS phản hồi quá chậm. Vui lòng thử lại sau.");
        }
    }
}
