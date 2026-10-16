using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using StreetBiz.Application.Common.Interfaces;

namespace StreetBiz.Infrastructure.Payments;

/// <summary>
/// SePay bank transfers. There is no checkout page: the payer scans a VietQR for
/// <see cref="SePaySettings.AccountNumber"/> whose content carries the payment code
/// ("SBP42" for transaction 42), and SePay's webhook reports the money once it lands.
/// The code is the transaction's provider reference, so the webhook matches the transaction
/// the same way a MoMo IPN does. Webhooks are authenticated by HMAC-SHA256
/// (<c>X-SePay-Signature: sha256=hex(HMAC(secret, "{timestamp}.{raw body}"))</c> with
/// <c>X-SePay-Timestamp</c>) or by <c>Authorization: Apikey {key}</c>.
/// </summary>
internal static class SePayGateway
{
    public const string HttpClientName = "sepay";

    public static string PaymentCode(SePaySettings sePay, long transactionId) =>
        sePay.PaymentCodePrefix.Trim().ToUpperInvariant() + transactionId.ToString(CultureInfo.InvariantCulture);

    public static PaymentGatewayCheckoutResult Create(SePaySettings sePay, PaymentGatewayCheckoutRequest request)
    {
        var code = PaymentCode(sePay, request.TransactionId);
        var instructions = Instructions(sePay, code, request.Amount);
        return new PaymentGatewayCheckoutResult(instructions.QrImageUrl, code, instructions);
    }

    public static BankTransferInstructions Instructions(SePaySettings sePay, string code, decimal amount)
    {
        // VND has no subunit: the QR and the matching both use whole dong.
        var wholeAmount = decimal.Round(amount, 0, MidpointRounding.AwayFromZero);
        var content = sePay.TransferContentTemplate.Replace("{code}", code, StringComparison.Ordinal);
        var qrImageUrl = $"{sePay.QrImageBaseUrl.TrimEnd('/')}" +
            $"?acc={Uri.EscapeDataString(sePay.AccountNumber!)}" +
            $"&bank={Uri.EscapeDataString(sePay.BankName!)}" +
            $"&amount={wholeAmount.ToString("0", CultureInfo.InvariantCulture)}" +
            $"&des={Uri.EscapeDataString(content)}";
        return new BankTransferInstructions(
            sePay.BankName!, sePay.AccountNumber!, sePay.AccountName, wholeAmount, content, qrImageUrl);
    }

    /// <summary>
    /// Our payment code inside what the bank passed on: SePay's own <c>code</c> when its
    /// payment-code rule is set up, otherwise the transfer content as the payer typed it
    /// (banks add their own references around it, change the case, or drop the spaces).
    /// </summary>
    public static string? FindCode(SePaySettings sePay, string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(sePay.PaymentCodePrefix))
        {
            return null;
        }

        var match = Regex.Match(
            text,
            $"(?<![A-Z]){Regex.Escape(sePay.PaymentCodePrefix.Trim())}(\\d{{1,18}})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
        return match.Success && long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? PaymentCode(sePay, id)
            : null;
    }

    public static PaymentGatewayCallback VerifyWebhook(
        SePaySettings sePay,
        string rawPayload,
        PaymentCallbackHeaders? headers,
        DateTimeOffset now)
    {
        var authentic = Authenticate(sePay, rawPayload, headers, now);
        try
        {
            using var json = JsonDocument.Parse(rawPayload);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new PaymentGatewayCallback(null, null, null, null, false, rawPayload);
            }

            var eventId = IdOf(root);
            if (!string.Equals(Str(root, "transferType"), "in", StringComparison.OrdinalIgnoreCase))
            {
                // Money leaving the account is never a payment to StreetBiz.
                return new PaymentGatewayCallback(null, null, null, null, authentic, rawPayload, eventId, Ignored: true);
            }

            var code = FindCode(sePay, Str(root, "code")) ?? FindCode(sePay, Str(root, "content"));
            // Money into another account linked to the same SePay company is not ours to apply:
            // left without a status, so it is recorded as REJECTED and someone is told.
            var intoOurAccount = string.IsNullOrWhiteSpace(sePay.AccountNumber)
                || Str(root, "accountNumber") == sePay.AccountNumber
                || Str(root, "subAccount") == sePay.AccountNumber;
            return new PaymentGatewayCallback(
                code,
                null,
                Dec(root, "transferAmount"),
                intoOurAccount ? "SUCCESS" : null,
                authentic,
                rawPayload,
                eventId);
        }
        catch (JsonException)
        {
            return new PaymentGatewayCallback(null, null, null, null, false, rawPayload);
        }
    }

    /// <summary>
    /// Looks for the transfer in the account's recent transactions through the My SePay API
    /// (GET /transactions/list, Bearer API token). The answer comes over TLS from SePay with our
    /// own token, so a match is trusted like an authenticated webhook. No match yet is a null
    /// status: "not final", checked again later.
    /// </summary>
    public static async Task<PaymentGatewayCallback> QueryAsync(
        HttpClient http,
        SePaySettings sePay,
        string paymentCode,
        CancellationToken cancellationToken)
    {
        var url = $"{sePay.ApiBaseUrl.TrimEnd('/')}/transactions/list?limit=100";
        if (!string.IsNullOrWhiteSpace(sePay.AccountNumber))
        {
            url += $"&account_number={Uri.EscapeDataString(sePay.AccountNumber)}";
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", sePay.ApiToken);
        using var response = await http.SendAsync(request, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new PaymentGatewayCallback(paymentCode, null, null, null, false, raw);
        }

        try
        {
            using var json = JsonDocument.Parse(raw);
            if (json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("transactions", out var transactions)
                && transactions.ValueKind == JsonValueKind.Array)
            {
                foreach (var transaction in transactions.EnumerateArray())
                {
                    var amountIn = Dec(transaction, "amount_in");
                    if (amountIn is not > 0)
                    {
                        continue;
                    }

                    var code = FindCode(sePay, Str(transaction, "code"))
                        ?? FindCode(sePay, Str(transaction, "transaction_content"));
                    if (code == paymentCode)
                    {
                        return new PaymentGatewayCallback(
                            code, null, amountIn, "SUCCESS", true, transaction.GetRawText(), IdOf(transaction));
                    }
                }
            }
        }
        catch (JsonException)
        {
            return new PaymentGatewayCallback(paymentCode, null, null, null, false, raw);
        }

        return new PaymentGatewayCallback(paymentCode, null, null, null, true);
    }

    /// <summary>SePay's transaction id inside a stored payload ("id", a number in webhooks, a string in the API).</summary>
    public static string? EventIdIn(string? rawPayload)
    {
        if (string.IsNullOrWhiteSpace(rawPayload))
        {
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(rawPayload);
            return json.RootElement.ValueKind == JsonValueKind.Object ? IdOf(json.RootElement) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool Authenticate(SePaySettings sePay, string rawPayload, PaymentCallbackHeaders? headers, DateTimeOffset now)
    {
        if (!string.IsNullOrWhiteSpace(sePay.WebhookSecret)
            && !string.IsNullOrWhiteSpace(headers?.Signature)
            && !string.IsNullOrWhiteSpace(headers.Timestamp))
        {
            return HmacMatches(sePay, rawPayload, headers.Signature, headers.Timestamp, now);
        }

        if (!string.IsNullOrWhiteSpace(sePay.WebhookApiKey)
            && headers?.Authorization is { } authorization
            && authorization.StartsWith("Apikey ", StringComparison.OrdinalIgnoreCase))
        {
            var supplied = Encoding.UTF8.GetBytes(authorization["Apikey ".Length..].Trim());
            var expected = Encoding.UTF8.GetBytes(sePay.WebhookApiKey.Trim());
            return supplied.Length == expected.Length && CryptographicOperations.FixedTimeEquals(supplied, expected);
        }

        return false;
    }

    private static bool HmacMatches(
        SePaySettings sePay, string rawPayload, string signature, string timestamp, DateTimeOffset now)
    {
        if (!long.TryParse(timestamp.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return false;
        }

        if (Math.Abs(now.ToUnixTimeSeconds() - seconds) > sePay.SignatureToleranceSeconds)
        {
            return false; // A replay of an old request, or a server clock that is far off.
        }

        var supplied = signature.Trim();
        if (supplied.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
        {
            supplied = supplied[7..];
        }

        var expected = Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(sePay.WebhookSecret!),
            Encoding.UTF8.GetBytes($"{timestamp.Trim()}.{rawPayload}"))).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(supplied.ToLowerInvariant()));
    }

    private static string? IdOf(JsonElement root) =>
        !root.TryGetProperty("id", out var id) ? null
        : id.ValueKind == JsonValueKind.Number ? id.GetRawText()
        : id.ValueKind == JsonValueKind.String ? id.GetString()
        : null;

    private static string? Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>A money field, sent as a number in webhooks and as a string ("900000.00") by the API.</summary>
    private static decimal? Dec(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String
            && decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
    }
}
