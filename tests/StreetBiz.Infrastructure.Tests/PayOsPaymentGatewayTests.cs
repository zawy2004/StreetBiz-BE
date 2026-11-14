using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Infrastructure.Payments;

namespace StreetBiz.Infrastructure.Tests;

/// <summary>
/// payOS paid straight to a payee's own channel: the signed payment link, the webhook signature,
/// the status API and the channel check, faked at the HTTP layer. Signatures are recomputed here
/// independently, following payOS's published JavaScript sample.
/// </summary>
public sealed class PayOsPaymentGatewayTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 3, 0, 0, TimeSpan.Zero);
    private static readonly PayeeCredentials Ward = new(11, "ward-client", "ward-api-key", "ward-checksum");

    // ------------------------------------------------------------------------------ checkout

    [Fact]
    public async Task A_payment_link_is_opened_on_the_payees_channel_with_the_documented_signature()
    {
        var api = new FakePayOs(CreatedLink(42001));
        var gateway = Gateway(api);

        var result = await gateway.CreateCheckoutAsync(FeeRequest(42), default);

        Assert.Equal("https://api.test/v2/payment-requests", api.LastUri);
        Assert.Equal("ward-client", api.LastClientId);
        Assert.Equal("ward-api-key", api.LastApiKey);
        using var body = JsonDocument.Parse(api.LastBody!);
        var sent = body.RootElement;
        Assert.Equal(42001, sent.GetProperty("orderCode").GetInt64());
        Assert.Equal(1_040_000, sent.GetProperty("amount").GetInt64());
        Assert.Equal("SB42", sent.GetProperty("description").GetString());
        Assert.Equal("http://web.test/vendor/finance/fees/3/payment", sent.GetProperty("returnUrl").GetString());
        Assert.Equal(sent.GetProperty("returnUrl").GetString(), sent.GetProperty("cancelUrl").GetString());
        Assert.Equal(Now.AddMinutes(30).ToUnixTimeSeconds(), sent.GetProperty("expiredAt").GetInt64());
        var expected = Hmac("ward-checksum",
            "amount=1040000&cancelUrl=http://web.test/vendor/finance/fees/3/payment&description=SB42" +
            "&orderCode=42001&returnUrl=http://web.test/vendor/finance/fees/3/payment");
        Assert.Equal(expected, sent.GetProperty("signature").GetString());

        Assert.Equal("PAYOS-42001", result.ProviderReference);
        Assert.Equal("https://pay.payos.vn/web/link-1", result.PaymentUrl);
        var qr = result.BankTransfer!;
        Assert.Equal("00020101021238570010A000000727QRDATA", qr.QrContent);
        Assert.Equal("MB Bank", qr.BankName);
        Assert.Equal("V3CAS0123456789", qr.AccountNumber);
        Assert.Equal("UBND PHUONG HAI CHAU", qr.AccountName);
        Assert.Equal(1_040_000m, qr.Amount);
        Assert.Equal("CSXXSB42", qr.TransferContent);
        Assert.Equal("https://pay.payos.vn/web/link-1", qr.CheckoutUrl);
        Assert.NotNull(result.CheckoutPayload);
    }

    [Fact]
    public async Task An_order_returns_to_its_order_page_and_a_new_attempt_gets_the_next_code()
    {
        var api = new FakePayOs(CreatedLink(77002));
        var gateway = Gateway(api);
        var first = await Gateway(new FakePayOs(CreatedLink(77001))).CreateCheckoutAsync(OrderRequest(77), default);

        await gateway.CreateCheckoutAsync(OrderRequest(77) with { PreviousCheckoutPayload = first.CheckoutPayload }, default);

        using var body = JsonDocument.Parse(api.LastBody!);
        Assert.Equal(77002, body.RootElement.GetProperty("orderCode").GetInt64());
        Assert.Equal("http://web.test/customer/orders/19/payment", body.RootElement.GetProperty("returnUrl").GetString());
    }

    [Fact]
    public async Task A_description_that_would_be_too_long_for_the_bank_is_shortened()
    {
        var api = new FakePayOs(CreatedLink(123456789001));
        await Gateway(api).CreateCheckoutAsync(FeeRequest(123_456_789), default);

        using var body = JsonDocument.Parse(api.LastBody!);
        Assert.Equal("StreetBiz", body.RootElement.GetProperty("description").GetString());
    }

    [Fact]
    public async Task Payos_needs_a_payee_is_never_simulated_and_can_be_switched_off()
    {
        var sandbox = new PaymentGatewaySettings { SandboxEnabled = true, PayOs = new PayOsSettings { Enabled = false } };
        var off = new ConfiguredPaymentGateway(Options.Create(sandbox), new FakeFactory(new FakePayOs("{}")));

        Assert.False(off.IsProviderAvailable("PAYOS"));
        Assert.True(off.IsProviderAvailable("MOMO")); // the sandbox still simulates the platform providers
        await Assert.ThrowsAsync<DomainRuleException>(() => off.CreateCheckoutAsync(FeeRequest(42), default));
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            Gateway(new FakePayOs(CreatedLink(42001))).CreateCheckoutAsync(FeeRequest(42) with { Payee = null }, default));
    }

    [Fact]
    public async Task Refused_keys_and_refused_links_are_reported_as_payee_channel_errors()
    {
        var unauthorized = await Assert.ThrowsAsync<PayeeChannelException>(() =>
            Gateway(new FakePayOs("{}", HttpStatusCode.Unauthorized)).CreateCheckoutAsync(FeeRequest(42), default));
        Assert.True(unauthorized.Unauthorized);

        var refused = await Assert.ThrowsAsync<PayeeChannelException>(() =>
            Gateway(new FakePayOs("""{"code":"231","desc":"Đơn thanh toán đã tồn tại","data":null}"""))
                .CreateCheckoutAsync(FeeRequest(42), default));
        Assert.False(refused.Unauthorized);
        Assert.Contains("Đơn thanh toán đã tồn tại", refused.Message);
        Assert.Equal(422, refused.StatusCode);
    }

    [Fact]
    public async Task A_stored_link_is_shown_again_until_it_expires()
    {
        var created = await Gateway(new FakePayOs(CreatedLink(42001))).CreateCheckoutAsync(FeeRequest(42), default);

        var again = Gateway(new FakePayOs("{}"), Now.AddMinutes(29)).DescribeStoredCheckout("PAYOS", created.CheckoutPayload, 1_040_000m);
        var expired = Gateway(new FakePayOs("{}"), Now.AddMinutes(30)).DescribeStoredCheckout("PAYOS", created.CheckoutPayload, 1_040_000m);

        Assert.Equal("00020101021238570010A000000727QRDATA", again?.QrContent);
        Assert.Null(expired);
        Assert.Null(Gateway(new FakePayOs("{}")).DescribeStoredCheckout("PAYOS", "not json", 1m));
        Assert.Null(Gateway(new FakePayOs("{}")).DescribeStoredCheckout("MOMO", created.CheckoutPayload, 1m));
    }

    // ------------------------------------------------------------------------------- webhook

    [Fact]
    public async Task A_webhook_signed_with_the_payees_checksum_key_is_a_payment()
    {
        var raw = Webhook(42001, 1_040_000, "ward-checksum");

        var callback = await Gateway(new FakePayOs("{}")).VerifyCallbackAsync(
            "PAYOS", raw, new PaymentCallbackHeaders(null) { Payee = Ward }, default);

        Assert.True(callback.SignatureValid);
        Assert.False(callback.Ignored);
        Assert.Equal("PAYOS-42001", callback.ProviderReference);
        Assert.Equal(1_040_000m, callback.Amount);
        Assert.Equal("SUCCESS", callback.Status);
        Assert.Equal("FT26306ABC123", callback.ProviderEventId);
    }

    [Fact]
    public async Task A_webhook_signed_with_another_key_or_altered_in_transit_is_not_authentic()
    {
        var gateway = Gateway(new FakePayOs("{}"));
        var headers = new PaymentCallbackHeaders(null) { Payee = Ward };

        var otherKey = await gateway.VerifyCallbackAsync("PAYOS", Webhook(42001, 1_040_000, "someone-elses-key"), headers, default);
        var altered = await gateway.VerifyCallbackAsync(
            "PAYOS", Webhook(42001, 1_040_000, "ward-checksum").Replace("1040000", "10400000"), headers, default);
        var noPayee = await gateway.VerifyCallbackAsync("PAYOS", Webhook(42001, 1_040_000, "ward-checksum"), null, default);

        Assert.False(otherKey.SignatureValid);
        Assert.False(altered.SignatureValid);
        Assert.False(noPayee.SignatureValid);
    }

    [Fact]
    public async Task Payos_test_webhook_is_acknowledged_and_ignored()
    {
        // payOS posts a sample (orderCode 123) when a webhook URL is registered: never one of ours.
        var callback = await Gateway(new FakePayOs("{}")).VerifyCallbackAsync(
            "PAYOS", Webhook(123, 3000, "ward-checksum"), new PaymentCallbackHeaders(null) { Payee = Ward }, default);

        Assert.True(callback.SignatureValid);
        Assert.True(callback.Ignored);
    }

    [Fact]
    public async Task A_webhook_that_does_not_report_a_payment_carries_no_status()
    {
        var callback = await Gateway(new FakePayOs("{}")).VerifyCallbackAsync(
            "PAYOS", Webhook(42001, 1_040_000, "ward-checksum", code: "01"), new PaymentCallbackHeaders(null) { Payee = Ward }, default);

        Assert.True(callback.SignatureValid);
        Assert.Null(callback.Status);
    }

    // --------------------------------------------------------------------------- status query

    [Theory]
    [InlineData("PAID", "SUCCESS")]
    [InlineData("CANCELLED", "FAILED")]
    [InlineData("EXPIRED", "FAILED")]
    [InlineData("PENDING", null)]
    [InlineData("PROCESSING", null)]
    [InlineData("UNDERPAID", null)]
    public async Task The_status_api_is_asked_with_the_payees_keys(string payosStatus, string? expected)
    {
        var api = new FakePayOs($$$"""
            {"code":"00","desc":"success","data":{"id":"link-1","orderCode":42001,"amount":1040000,"amountPaid":1040000,
            "amountRemaining":0,"status":"{{{payosStatus}}}","transactions":[{"reference":"FT26306ABC123","amount":1040000}]}}
            """);

        var state = await Gateway(api).QueryPaymentAsync("PAYOS", "PAYOS-42001", Ward, default);

        Assert.Equal("https://api.test/v2/payment-requests/42001", api.LastUri);
        Assert.Equal("ward-client", api.LastClientId);
        Assert.Equal(expected, state?.Status);
        Assert.True(state!.SignatureValid);
        if (expected == "SUCCESS")
        {
            Assert.Equal(1_040_000m, state.Amount);
            Assert.Equal("FT26306ABC123", state.ProviderEventId);
        }
    }

    [Fact]
    public async Task A_busy_or_failing_payos_reads_as_not_final_but_refused_keys_are_reported()
    {
        var busy = await Gateway(new FakePayOs("{}", HttpStatusCode.TooManyRequests)).QueryPaymentAsync("PAYOS", "PAYOS-42001", Ward, default);
        var down = await Gateway(new FakePayOs("oops", HttpStatusCode.BadGateway)).QueryPaymentAsync("PAYOS", "PAYOS-42001", Ward, default);
        var unknown = await Gateway(new FakePayOs("""{"code":"101","desc":"Không tìm thấy"}""")).QueryPaymentAsync("PAYOS", "PAYOS-42001", Ward, default);

        Assert.Null(busy?.Status);
        Assert.Null(down?.Status);
        Assert.Null(unknown?.Status);
        var refused = await Assert.ThrowsAsync<PayeeChannelException>(() =>
            Gateway(new FakePayOs("{}", HttpStatusCode.Unauthorized)).QueryPaymentAsync("PAYOS", "PAYOS-42001", Ward, default));
        Assert.True(refused.Unauthorized);
    }

    // -------------------------------------------------------------------------- channel check

    [Fact]
    public async Task Connecting_registers_the_webhook_and_reads_back_the_bank_account()
    {
        var api = new FakePayOs("""
            {"code":"00","desc":"success","data":{"webhookUrl":"https://api.example/api/payments/payos/callback/tok",
            "accountNumber":"0123456789","accountName":"NGUYEN THI LAN","name":"Ngân hàng TMCP Quân đội","shortName":"MB Bank"}}
            """);
        var channel = Channel(api, publicBase: "https://api.example/");

        var url = channel.WebhookUrlFor("tok");
        var info = await channel.VerifyChannelAsync(Ward, url, default);

        Assert.Equal("https://api.example/api/payments/payos/callback/tok", url);
        Assert.Equal("https://api.test/confirm-webhook", api.LastUri);
        Assert.Contains("\"webhookUrl\":\"https://api.example/api/payments/payos/callback/tok\"", api.LastBody);
        Assert.Equal(new PayeeChannelInfo("MB Bank", "0123456789", "NGUYEN THI LAN", true), info);
    }

    [Fact]
    public async Task Without_a_public_url_only_the_keys_are_checked()
    {
        var api = new FakePayOs("""{"code":"101","desc":"Không tìm thấy"}""");
        var channel = Channel(api, publicBase: null);

        Assert.Null(channel.WebhookUrlFor("tok"));
        var info = await channel.VerifyChannelAsync(Ward, null, default);

        Assert.Equal("https://api.test/v2/payment-requests/1", api.LastUri);
        Assert.False(info.WebhookConfirmed);
    }

    [Fact]
    public async Task Wrong_keys_an_unreachable_url_and_a_switched_off_payos_are_explained()
    {
        var wrongKeys = await Assert.ThrowsAsync<PayeeChannelException>(() =>
            Channel(new FakePayOs("{}", HttpStatusCode.Unauthorized), "https://api.example").VerifyChannelAsync(Ward, "https://api.example/x", default));
        Assert.True(wrongKeys.Unauthorized);

        var badUrl = await Assert.ThrowsAsync<PayeeChannelException>(() =>
            Channel(new FakePayOs("{}", HttpStatusCode.BadRequest), "https://api.example").VerifyChannelAsync(Ward, "https://api.example/x", default));
        Assert.Contains("PublicWebhookBaseUrl", badUrl.Message);

        var off = new PayOsChannelGateway(
            Options.Create(new PaymentGatewaySettings { PayOs = new PayOsSettings { Enabled = false } }),
            new FakeFactory(new FakePayOs("{}")));
        await Assert.ThrowsAsync<PayeeChannelException>(() => off.VerifyChannelAsync(Ward, null, default));
    }

    // ------------------------------------------------------------------------------- helpers

    private static ConfiguredPaymentGateway Gateway(FakePayOs api, DateTimeOffset? now = null) =>
        new(Options.Create(Settings()), new FakeFactory(api), new FixedClock(now ?? Now));

    private static PayOsChannelGateway Channel(FakePayOs api, string? publicBase)
    {
        var settings = Settings();
        settings.PayOs.PublicWebhookBaseUrl = publicBase;
        return new PayOsChannelGateway(Options.Create(settings), new FakeFactory(api));
    }

    private static PaymentGatewaySettings Settings() => new()
    {
        PayOs = new PayOsSettings { ApiBaseUrl = "https://api.test/", FrontendBaseUrl = "http://web.test/", LinkLifetimeMinutes = 30 },
    };

    private static PaymentGatewayCheckoutRequest FeeRequest(long transactionId) =>
        new(3, "Kỳ 3/3", transactionId, "key-1", "PAYOS", 1_040_000m, "RENTAL_FEE") { Payee = Ward };

    private static PaymentGatewayCheckoutRequest OrderRequest(long transactionId) =>
        new(19, "SB-000019", transactionId, "key-1", "PAYOS", 65_000m) { Payee = Ward };

    private static string CreatedLink(long orderCode) => $$"""
        {"code":"00","desc":"success","data":{"bin":"970422","accountNumber":"V3CAS0123456789",
        "accountName":"UBND PHUONG HAI CHAU","amount":1040000,"description":"CSXXSB42","orderCode":{{orderCode}},
        "currency":"VND","paymentLinkId":"link-1","status":"PENDING","checkoutUrl":"https://pay.payos.vn/web/link-1",
        "qrCode":"00020101021238570010A000000727QRDATA"},"signature":"ignored"}
        """;

    /// <summary>A payOS webhook whose data is signed the way payOS's JavaScript sample signs it.</summary>
    private static string Webhook(long orderCode, long amount, string checksumKey, string code = "00")
    {
        var data = new JsonObject
        {
            ["orderCode"] = orderCode,
            ["amount"] = amount,
            ["description"] = "CSXXSB42",
            ["accountNumber"] = "V3CAS0123456789",
            ["reference"] = "FT26306ABC123",
            ["transactionDateTime"] = "2026-11-02 10:05:00",
            ["currency"] = "VND",
            ["paymentLinkId"] = "link-1",
            ["code"] = code,
            ["desc"] = "success",
            ["counterAccountBankId"] = "",
            ["counterAccountBankName"] = null,
            ["counterAccountName"] = "null",
            ["counterAccountNumber"] = null,
            ["virtualAccountName"] = "",
            ["virtualAccountNumber"] = "",
        };
        var root = new JsonObject
        {
            ["code"] = code,
            ["desc"] = "success",
            ["success"] = code == "00",
            ["data"] = data,
            ["signature"] = Hmac(checksumKey, ReferenceSignedText(data)),
        };
        return root.ToJsonString();
    }

    /// <summary>
    /// payOS's own algorithm, written independently of the gateway: keys sorted; null, "null" and
    /// "undefined" as empty; "key=value" joined by "&amp;".
    /// </summary>
    private static string ReferenceSignedText(JsonObject data) => string.Join("&", data
        .OrderBy(p => p.Key, StringComparer.Ordinal)
        .Select(p =>
        {
            var value = p.Value is null ? "" : p.Value.GetValueKind() == JsonValueKind.String ? p.Value.GetValue<string>() : p.Value.ToJsonString();
            return $"{p.Key}={(value is "null" or "undefined" ? "" : value)}";
        }));

    private static string Hmac(string key, string text) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private sealed class FakePayOs(string responseJson, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string? LastUri { get; private set; }
        public string? LastBody { get; private set; }
        public string? LastClientId { get; private set; }
        public string? LastApiKey { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUri = request.RequestUri!.ToString();
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            LastClientId = request.Headers.TryGetValues("x-client-id", out var client) ? client.Single() : null;
            LastApiKey = request.Headers.TryGetValues("x-api-key", out var key) ? key.Single() : null;
            return new HttpResponseMessage(status) { Content = new StringContent(responseJson, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class FakeFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
