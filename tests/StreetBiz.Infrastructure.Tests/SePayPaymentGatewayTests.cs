using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Infrastructure.Payments;

namespace StreetBiz.Infrastructure.Tests;

/// <summary>SePay bank transfers: the VietQR, webhook authentication and parsing, and the status API, faked at the HTTP layer.</summary>
public sealed class SePayPaymentGatewayTests
{
    private const string Secret = "test-sepay-webhook-secret";
    private const string ApiKey = "test-sepay-api-key";
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Checkout_returns_a_vietqr_whose_content_is_the_payment_code()
    {
        var gateway = Gateway();

        var result = await gateway.CreateCheckoutAsync(
            new PaymentGatewayCheckoutRequest(3, "Kỳ 2/3", 42, "key-1", "SEPAY", 900_000m, "RENTAL_FEE"), default);

        Assert.True(gateway.IsProviderAvailable("SEPAY"));
        Assert.Equal("SBP42", result.ProviderReference);
        Assert.NotNull(result.BankTransfer);
        Assert.Equal("SBP42", result.BankTransfer!.TransferContent);
        Assert.Equal(900_000m, result.BankTransfer.Amount);
        Assert.Equal("0123456789", result.BankTransfer.AccountNumber);
        Assert.Equal("https://qr.sepay.vn/img?acc=0123456789&bank=MBBank&amount=900000&des=SBP42", result.PaymentUrl);
        Assert.Equal(result.PaymentUrl, result.BankTransfer.QrImageUrl);
    }

    [Fact]
    public async Task Without_its_account_sepay_is_not_offered_and_never_builds_a_qr()
    {
        var gateway = new ConfiguredPaymentGateway(Options.Create(new PaymentGatewaySettings()));

        Assert.False(gateway.IsProviderAvailable("SEPAY"));
        Assert.Null(gateway.DescribeBankTransfer("SEPAY", "SBP42", 900_000m));
        await Assert.ThrowsAsync<StreetBiz.Application.Common.Exceptions.DomainRuleException>(() =>
            gateway.CreateCheckoutAsync(new PaymentGatewayCheckoutRequest(3, "X", 42, "k", "SEPAY", 1m), default));
    }

    [Fact]
    public void The_same_qr_is_described_again_from_the_reference_and_amount()
    {
        var qr = Gateway().DescribeBankTransfer("SEPAY", "SBP42", 900_000m);

        Assert.NotNull(qr);
        Assert.Equal("SBP42", qr!.TransferContent);
        Assert.Contains("amount=900000", qr.QrImageUrl);
    }

    [Fact]
    public async Task A_correctly_signed_webhook_is_accepted_and_a_tampered_one_is_not()
    {
        var gateway = Gateway();
        var body = Webhook(id: 92704, amount: 900_000, content: "SBP42 chuyen tien");
        var timestamp = Now.ToUnixTimeSeconds().ToString();

        var genuine = await gateway.VerifyCallbackAsync("SEPAY", body, Hmac(body, timestamp), default);
        var tampered = await gateway.VerifyCallbackAsync(
            "SEPAY", body.Replace("900000", "100"), Hmac(body, timestamp), default);

        Assert.True(genuine.SignatureValid);
        Assert.Equal("SUCCESS", genuine.Status);
        Assert.Equal("SBP42", genuine.ProviderReference);
        Assert.Equal(900_000m, genuine.Amount);
        Assert.Equal("92704", genuine.ProviderEventId);
        Assert.False(genuine.Ignored);
        Assert.False(tampered.SignatureValid);
    }

    [Fact]
    public async Task A_signed_webhook_replayed_long_after_it_was_signed_is_refused()
    {
        var body = Webhook(id: 1, amount: 900_000, content: "SBP42");
        var tenMinutesAgo = Now.AddMinutes(-10).ToUnixTimeSeconds().ToString();

        var replayed = await Gateway().VerifyCallbackAsync("SEPAY", body, Hmac(body, tenMinutesAgo), default);

        Assert.False(replayed.SignatureValid);
    }

    [Fact]
    public async Task The_api_key_authenticates_the_webhook_and_anything_else_does_not()
    {
        var gateway = Gateway();
        var body = Webhook(id: 1, amount: 900_000, content: "SBP42");

        var withKey = await gateway.VerifyCallbackAsync("SEPAY", body, new PaymentCallbackHeaders(null, null, $"Apikey {ApiKey}"), default);
        var wrongKey = await gateway.VerifyCallbackAsync("SEPAY", body, new PaymentCallbackHeaders(null, null, "Apikey nope"), default);
        var bearer = await gateway.VerifyCallbackAsync("SEPAY", body, new PaymentCallbackHeaders(null, null, $"Bearer {ApiKey}"), default);
        var nothing = await gateway.VerifyCallbackAsync("SEPAY", body, null, default);

        Assert.True(withKey.SignatureValid);
        Assert.False(wrongKey.SignatureValid);
        Assert.False(bearer.SignatureValid);
        Assert.False(nothing.SignatureValid);
    }

    [Theory]
    [InlineData("SBP42 chuyen tien", "SBP42")]
    [InlineData("MBVCB.1234567.sbp42.CT tu 0123 NGUYEN VAN A", "SBP42")] // bank noise, lower case
    [InlineData("NGUYEN VAN A chuyen tien SBP042", "SBP42")]
    [InlineData("SBP42CHUYEN TIEN", "SBP42")]
    [InlineData("tien phi thue o", null)] // the payer forgot the code
    [InlineData("XSBP42", null)] // part of another word
    public async Task The_payment_code_is_read_from_the_transfer_content(string content, string? expected)
    {
        var body = Webhook(id: 7, amount: 900_000, content: content);

        var callback = await Gateway().VerifyCallbackAsync("SEPAY", body, ApiKeyHeaders(), default);

        Assert.Equal(expected, callback.ProviderReference);
    }

    [Fact]
    public async Task Sepays_own_code_field_is_preferred_when_its_payment_code_rule_is_set_up()
    {
        var body = Webhook(id: 7, amount: 900_000, content: "chuyen tien", code: "SBP42");

        var callback = await Gateway().VerifyCallbackAsync("SEPAY", body, ApiKeyHeaders(), default);

        Assert.Equal("SBP42", callback.ProviderReference);
    }

    [Fact]
    public async Task Money_leaving_the_account_is_ignored()
    {
        var body = Webhook(id: 8, amount: 50_000, content: "SBP42", transferType: "out");

        var callback = await Gateway().VerifyCallbackAsync("SEPAY", body, ApiKeyHeaders(), default);

        Assert.True(callback.Ignored);
        Assert.Null(callback.Status);
    }

    [Fact]
    public async Task Money_into_another_linked_account_is_not_a_success()
    {
        var body = Webhook(id: 9, amount: 900_000, content: "SBP42", accountNumber: "9999999999");

        var callback = await Gateway().VerifyCallbackAsync("SEPAY", body, ApiKeyHeaders(), default);

        Assert.True(callback.SignatureValid);
        Assert.Null(callback.Status);
    }

    [Fact]
    public async Task The_status_api_finds_the_transfer_by_its_payment_code()
    {
        var api = new FakeSePayApi("""
            {"status":200,"error":null,"messages":{"success":true},"transactions":[
              {"id":"49690","amount_in":"0.00","amount_out":"50000.00","transaction_content":"SBP42 hoan tien","code":null},
              {"id":"49682","amount_in":"900000.00","amount_out":"0.00","transaction_content":"MBVCB.88.SBP42.CT tu A","code":null},
              {"id":"49681","amount_in":"25000.00","amount_out":"0.00","transaction_content":"SBP41","code":"SBP41"}
            ]}
            """);

        var status = await Gateway(api).QueryPaymentAsync("SEPAY", "SBP42", default);

        Assert.NotNull(status);
        Assert.Equal("SUCCESS", status!.Status);
        Assert.Equal(900_000m, status.Amount);
        Assert.Equal("49682", status.ProviderEventId);
        Assert.True(status.SignatureValid);
        Assert.Equal("Bearer test-sepay-api-token", api.LastAuthorization);
        Assert.Contains("/transactions/list?limit=100&account_number=0123456789", api.LastUri);
    }

    [Fact]
    public async Task No_transfer_yet_is_not_final()
    {
        var api = new FakeSePayApi("""{"status":200,"transactions":[]}""");

        var status = await Gateway(api).QueryPaymentAsync("SEPAY", "SBP42", default);

        Assert.NotNull(status);
        Assert.Null(status!.Status);
    }

    [Fact]
    public async Task Without_an_api_token_sepay_is_not_asked()
    {
        var settings = Settings();
        settings.SePay.ApiToken = null;
        var gateway = new ConfiguredPaymentGateway(Options.Create(settings), new FakeFactory(new FakeSePayApi("{}")), new FixedClock(Now));

        Assert.Null(await gateway.QueryPaymentAsync("SEPAY", "SBP42", default));
    }

    private static ConfiguredPaymentGateway Gateway(FakeSePayApi? api = null) =>
        new(Options.Create(Settings()), new FakeFactory(api ?? new FakeSePayApi("{}")), new FixedClock(Now));

    private static PaymentGatewaySettings Settings() => new()
    {
        SePay = new SePaySettings
        {
            BankName = "MBBank",
            AccountNumber = "0123456789",
            AccountName = "UBND PHUONG HAI CHAU 1",
            WebhookSecret = Secret,
            WebhookApiKey = ApiKey,
            ApiToken = "test-sepay-api-token",
        },
    };

    private static string Webhook(
        long id, long amount, string content, string? code = null, string transferType = "in",
        string accountNumber = "0123456789") =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            id,
            gateway = "MBBank",
            transactionDate = "2026-10-16 10:00:00",
            accountNumber,
            subAccount = (string?)null,
            code,
            content,
            transferType,
            description = "BankAPINotify " + content,
            transferAmount = amount,
            accumulated = 5_000_000,
            referenceCode = "FT26289123456",
        });

    private static PaymentCallbackHeaders Hmac(string body, string timestamp)
    {
        var signature = Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}"))).ToLowerInvariant();
        return new PaymentCallbackHeaders("sha256=" + signature, timestamp);
    }

    private static PaymentCallbackHeaders ApiKeyHeaders() => new(null, null, $"Apikey {ApiKey}");

    private sealed class FakeSePayApi(string responseJson) : HttpMessageHandler
    {
        public string? LastUri { get; private set; }
        public string? LastAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUri = request.RequestUri!.ToString();
            LastAuthorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            });
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
