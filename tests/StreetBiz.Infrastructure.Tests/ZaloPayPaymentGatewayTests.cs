using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Infrastructure.Payments;

namespace StreetBiz.Infrastructure.Tests;

/// <summary>
/// ZaloPay Gateway v2 signing, callback verification and result mapping, with ZaloPay faked at the HTTP layer.
/// The expected MACs are computed here, independently of the adapter, from the formulas in ZaloPay's
/// documentation. NOT run against ZaloPay's real sandbox (no credentials were available).
/// </summary>
public sealed class ZaloPayPaymentGatewayTests
{
    private const string AppId = "2553";
    private const string Key1 = "PcY4iZIKFCIdgZvA6ueMcMHHUbRLYjPL";
    private const string Key2 = "kLtgPl8HHhfvMuDHPwKfgfsY4Ydm9eIz";

    // 23 Oct 2026 01:00 in Da Nang is still 22 Oct 18:00 UTC: the order id carries the Vietnamese day.
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Create_posts_a_form_signed_with_key1_in_the_documented_field_order_and_returns_the_order_url()
    {
        var zalo = new FakeZaloPay("""{"return_code":1,"return_message":"Giao dịch thành công","order_url":"https://sbgateway.zalopay.vn/openinapp?order=abc","zp_trans_token":"t"}""");
        var gateway = Gateway(zalo);

        var result = await gateway.CreateCheckoutAsync(
            new PaymentGatewayCheckoutRequest(19, "SB-000019", 42, "key-1", "ZALOPAY", 50_000m), default);

        Assert.Equal("https://sbgateway.zalopay.vn/openinapp?order=abc", result.PaymentUrl);
        Assert.Matches("^261023_SB42[0-9a-f]{8}$", result.ProviderReference);
        Assert.Equal("https://sb-openapi.zalopay.vn/v2/create", zalo.LastUri);

        var form = zalo.LastForm!;
        Assert.Equal(AppId, form["app_id"]);
        Assert.Equal(result.ProviderReference, form["app_trans_id"]);
        Assert.Equal("50000", form["amount"]);
        Assert.Equal("http://localhost:5023/api/payments/zalopay/callback", form["callback_url"]);
        Assert.Equal(DateTimeOffset.Parse("2026-10-22T18:00:00Z").ToUnixTimeMilliseconds().ToString(), form["app_time"]);
        // embed_data carries where the buyer returns to and the idempotency key the callback will echo back.
        using var embed = JsonDocument.Parse(form["embed_data"]);
        Assert.Equal("http://localhost:5173/customer/orders/19/payment", embed.RootElement.GetProperty("redirecturl").GetString());
        Assert.Equal("key-1", embed.RootElement.GetProperty("idempotencyKey").GetString());
        // The item is a JSON array, as ZaloPay requires.
        using var item = JsonDocument.Parse(form["item"]);
        Assert.Equal(JsonValueKind.Array, item.RootElement.ValueKind);

        var expected = HexHmac(Key1,
            $"{AppId}|{form["app_trans_id"]}|{form["app_user"]}|{form["amount"]}|{form["app_time"]}|{form["embed_data"]}|{form["item"]}");
        Assert.Equal(expected, form["mac"]);
    }

    [Fact]
    public async Task A_retried_checkout_gets_a_new_order_id_because_ZaloPay_refuses_a_repeated_one()
    {
        var gateway = Gateway(new FakeZaloPay("""{"return_code":1,"return_message":"ok","order_url":"https://sbgateway.zalopay.vn/x"}"""));
        var request = new PaymentGatewayCheckoutRequest(19, "SB-000019", 42, "key-1", "ZALOPAY", 50_000m);

        var first = await gateway.CreateCheckoutAsync(request, default);
        var retry = await gateway.CreateCheckoutAsync(request, default);

        Assert.StartsWith("261023_SB42", first.ProviderReference);
        Assert.NotEqual(first.ProviderReference, retry.ProviderReference);
    }

    [Fact]
    public async Task The_fee_and_penalty_pages_get_their_own_return_url()
    {
        var zalo = new FakeZaloPay("""{"return_code":1,"return_message":"ok","order_url":"https://sbgateway.zalopay.vn/x"}""");
        var gateway = Gateway(zalo);

        await gateway.CreateCheckoutAsync(
            new PaymentGatewayCheckoutRequest(7, "FEE-7", 43, "key-2", "ZALOPAY", 100_000m, "RENTAL_FEE"), default);

        using var embed = JsonDocument.Parse(zalo.LastForm!["embed_data"]);
        Assert.Equal("http://localhost:5173/vendor/finance/fees/7", embed.RootElement.GetProperty("redirecturl").GetString());
    }

    [Fact]
    public async Task A_refused_create_surfaces_ZaloPays_message_and_code()
    {
        var gateway = Gateway(new FakeZaloPay("""{"return_code":2,"return_message":"Giao dịch thất bại","sub_return_code":-49}"""));

        var error = await Assert.ThrowsAsync<DomainRuleException>(() =>
            gateway.CreateCheckoutAsync(new PaymentGatewayCheckoutRequest(1, "X", 1, "k", "ZALOPAY", 10_000m), default));

        Assert.Contains("Giao dịch thất bại", error.Message);
        Assert.Contains("mã 2", error.Message);
    }

    [Fact]
    public async Task A_reply_that_is_not_json_is_reported_not_swallowed()
    {
        var gateway = Gateway(new FakeZaloPay("<html>502 Bad Gateway</html>"));

        await Assert.ThrowsAsync<DomainRuleException>(() =>
            gateway.CreateCheckoutAsync(new PaymentGatewayCheckoutRequest(1, "X", 1, "k", "ZALOPAY", 10_000m), default));
    }

    [Fact]
    public async Task A_genuine_callback_is_accepted_and_every_kind_of_tampering_is_not()
    {
        var gateway = Gateway(new FakeZaloPay("{}"));
        var body = Callback(amount: 50_000);

        var genuine = await gateway.VerifyCallbackAsync("ZALOPAY", body, null, default);

        Assert.True(genuine.SignatureValid);
        Assert.Equal("SUCCESS", genuine.Status);
        Assert.Equal("261023_SB42abcd1234", genuine.ProviderReference);
        Assert.Equal(50_000m, genuine.Amount);
        Assert.Equal("key-1", genuine.IdempotencyKey);

        // data edited after it was signed (a smaller amount): the MAC no longer matches.
        Assert.False((await gateway.VerifyCallbackAsync("ZALOPAY", body.Replace("50000", "1000"), null, default)).SignatureValid);
        // The mac itself altered.
        using var doc = JsonDocument.Parse(body);
        var mac = doc.RootElement.GetProperty("mac").GetString()!;
        Assert.False((await gateway.VerifyCallbackAsync("ZALOPAY", body.Replace(mac, new string('0', mac.Length)), null, default)).SignatureValid);
        // Signed with key1 (the request key) instead of key2: not a callback ZaloPay sent.
        Assert.False((await gateway.VerifyCallbackAsync("ZALOPAY", Callback(50_000, key: Key1), null, default)).SignatureValid);
    }

    [Fact]
    public async Task A_callback_signed_correctly_but_for_another_app_is_refused()
    {
        var gateway = Gateway(new FakeZaloPay("{}"));

        var result = await gateway.VerifyCallbackAsync("ZALOPAY", Callback(50_000, appId: "9999"), null, default);

        Assert.False(result.SignatureValid);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"data":"not json","mac":"00"}""")]
    [InlineData("""{"data":"{}"}""")]
    public async Task A_garbled_callback_is_never_valid(string body)
    {
        var gateway = Gateway(new FakeZaloPay("{}"));

        var result = await gateway.VerifyCallbackAsync("ZALOPAY", body, null, default);

        Assert.False(result.SignatureValid);
    }

    [Theory]
    [InlineData(1, "SUCCESS")]
    [InlineData(2, "FAILED")]
    [InlineData(3, null)] // still processing: must not cancel an order that may be paid
    [InlineData(-49, null)]
    [InlineData(99, null)]
    public async Task Query_is_signed_with_key1_and_maps_only_final_codes_to_a_status(int returnCode, string? expected)
    {
        var zalo = new FakeZaloPay($$"""{"return_code":{{returnCode}},"return_message":"x","is_processing":false,"amount":50000,"zp_trans_id":1}""");
        var gateway = Gateway(zalo);

        var status = await gateway.QueryPaymentAsync("ZALOPAY", "261023_SB42abcd1234", default);

        Assert.NotNull(status);
        Assert.Equal(expected, status!.Status);
        Assert.Equal("261023_SB42abcd1234", status.ProviderReference);
        Assert.True(status.SignatureValid);
        Assert.Equal("https://sb-openapi.zalopay.vn/v2/query", zalo.LastUri);
        Assert.Equal(AppId, zalo.LastForm!["app_id"]);
        Assert.Equal(HexHmac(Key1, $"{AppId}|261023_SB42abcd1234|{Key1}"), zalo.LastForm["mac"]);
    }

    [Fact]
    public async Task A_query_that_comes_back_as_an_error_page_is_not_trusted()
    {
        var gateway = Gateway(new FakeZaloPay("{}", HttpStatusCode.BadGateway));

        var status = await gateway.QueryPaymentAsync("ZALOPAY", "261023_SB42abcd1234", default);

        Assert.NotNull(status);
        Assert.False(status!.SignatureValid);
        Assert.Null(status.Status);
    }

    [Fact]
    public void It_stays_off_until_it_is_switched_on_and_every_credential_is_present()
    {
        var gateway = (PaymentGatewaySettings s) =>
            new ConfiguredPaymentGateway(Options.Create(s), new FakeFactory(new FakeZaloPay("{}")), new FixedTime(Now));

        // Credentials without the switch.
        var off = Settings();
        off.ZaloPay.Enabled = false;
        Assert.False(gateway(off).IsProviderAvailable("ZALOPAY"));

        // The switch without every credential.
        foreach (var clear in new Action<PaymentProviderSettings>[]
        {
            z => z.AppId = null,
            z => z.Key1 = "",
            z => z.Key2 = " ",
            z => z.ApiBaseUrl = null,
            z => z.ReturnUrl = null,
            z => z.CallbackUrl = null,
        })
        {
            var partial = Settings();
            clear(partial.ZaloPay);
            Assert.False(gateway(partial).IsProviderAvailable("ZALOPAY"));
        }

        Assert.True(gateway(Settings()).IsProviderAvailable("ZALOPAY"));
        // Configuring ZaloPay never makes MoMo look available.
        Assert.False(gateway(Settings()).IsProviderAvailable("MOMO"));
    }

    [Fact]
    public async Task When_off_a_checkout_is_refused_instead_of_calling_out()
    {
        var zalo = new FakeZaloPay("{}");
        var settings = Settings();
        settings.ZaloPay.Enabled = false;
        var gateway = new ConfiguredPaymentGateway(Options.Create(settings), new FakeFactory(zalo), new FixedTime(Now));

        await Assert.ThrowsAsync<DomainRuleException>(() =>
            gateway.CreateCheckoutAsync(new PaymentGatewayCheckoutRequest(1, "X", 1, "k", "ZALOPAY", 10_000m), default));

        Assert.Null(zalo.LastUri);
        Assert.Null(await gateway.QueryPaymentAsync("ZALOPAY", "261023_SB1x", default));
    }

    [Fact]
    public async Task The_generic_signed_callback_format_still_works_for_a_url_template_zalopay()
    {
        // A deployment that only has a checkout URL template keeps the HMAC-header format it always had.
        const string raw = "{\"idempotencyKey\":\"key\",\"amount\":25000,\"status\":\"SUCCESS\"}";
        var settings = new PaymentGatewaySettings { ZaloPay = new PaymentProviderSettings { CallbackSecret = "template-secret" } };
        var gateway = new ConfiguredPaymentGateway(Options.Create(settings), new FakeFactory(new FakeZaloPay("{}")), new FixedTime(Now));

        var valid = await gateway.VerifyCallbackAsync("ZALOPAY", raw, HexHmac("template-secret", raw), default);
        var invalid = await gateway.VerifyCallbackAsync("ZALOPAY", raw, "00", default);

        Assert.True(valid.SignatureValid);
        Assert.False(invalid.SignatureValid);
    }

    private static PaymentGatewaySettings Settings() => new()
    {
        ZaloPay = new PaymentProviderSettings
        {
            Enabled = true,
            ApiBaseUrl = "https://sb-openapi.zalopay.vn/v2",
            AppId = AppId,
            Key1 = Key1,
            Key2 = Key2,
            ReturnUrl = "http://localhost:5173/customer/orders/{referenceId}/payment",
            FeeReturnUrl = "http://localhost:5173/vendor/finance/fees/{referenceId}",
            CallbackUrl = "http://localhost:5023/api/payments/zalopay/callback",
        },
    };

    private static ConfiguredPaymentGateway Gateway(FakeZaloPay zalo) =>
        new(Options.Create(Settings()), new FakeFactory(zalo), new FixedTime(Now));

    private static string HexHmac(string key, string data)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(data))).ToLowerInvariant();
    }

    /// <summary>A callback as ZaloPay sends it: {"data": "<json string>", "mac": hmac-sha256(key2, data), "type": 1}.</summary>
    private static string Callback(long amount, string key = Key2, string appId = AppId)
    {
        var embed = JsonSerializer.Serialize(new { redirecturl = "http://localhost:5173/x", idempotencyKey = "key-1" });
        var data = JsonSerializer.Serialize(new
        {
            app_id = long.Parse(appId),
            app_trans_id = "261023_SB42abcd1234",
            app_time = 1790000000000,
            app_user = "StreetBiz",
            amount,
            embed_data = embed,
            item = "[]",
            zp_trans_id = 210000000001,
            server_time = 1790000005000,
            channel = 38,
            merchant_user_id = "x",
            user_fee_amount = 0,
            discount_amount = 0,
        });
        return JsonSerializer.Serialize(new { data, mac = HexHmac(key, data), type = 1 });
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeZaloPay(string responseJson, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string? LastUri { get; private set; }
        public Dictionary<string, string>? LastForm { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUri = request.RequestUri!.ToString();
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            LastForm = body.Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(pair => pair.Split('=', 2))
                .ToDictionary(p => Uri.UnescapeDataString(p[0].Replace('+', ' ')), p => Uri.UnescapeDataString((p.Length > 1 ? p[1] : "").Replace('+', ' ')));
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class FakeFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
