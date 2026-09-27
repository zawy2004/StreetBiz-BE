using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Infrastructure.Payments;

namespace StreetBiz.Infrastructure.Tests;

/// <summary>MoMo v2 signing, IPN verification and result-code mapping, with MoMo faked at the HTTP layer.</summary>
public sealed class MomoPaymentGatewayTests
{
    private const string AccessKey = "F8BBA842ECF85";
    private const string SecretKey = "K951B6PE1waDMi640xX08PD3vg6EkVlz";

    [Fact]
    public async Task Checkout_sends_a_correctly_signed_create_request_and_returns_the_pay_url()
    {
        var momo = new FakeMomo("""{"resultCode":0,"message":"Thành công.","payUrl":"https://test-payment.momo.vn/v2/gateway/pay?t=abc"}""");
        var gateway = Gateway(momo);

        var result = await gateway.CreateCheckoutAsync(
            new PaymentGatewayCheckoutRequest(19, "SB-000019", 42, "key-1", "MOMO", 50_000m), default);

        Assert.Equal("https://test-payment.momo.vn/v2/gateway/pay?t=abc", result.PaymentUrl);
        Assert.Equal("SB-T42", result.ProviderReference);
        Assert.EndsWith("/create", momo.LastUri);
        using var sent = JsonDocument.Parse(momo.LastBody!);
        var body = sent.RootElement;
        Assert.Equal("http://localhost:5173/customer/orders/19/payment", body.GetProperty("redirectUrl").GetString());
        Assert.Equal("50000", body.GetProperty("amount").GetString());
        var raw = $"accessKey={AccessKey}&amount=50000&extraData=&ipnUrl={body.GetProperty("ipnUrl").GetString()}" +
            $"&orderId=SB-T42&orderInfo={body.GetProperty("orderInfo").GetString()}&partnerCode=MOMO" +
            $"&redirectUrl={body.GetProperty("redirectUrl").GetString()}&requestId={body.GetProperty("requestId").GetString()}" +
            "&requestType=captureWallet";
        Assert.Equal(Sign(raw), body.GetProperty("signature").GetString());
    }

    [Fact]
    public async Task A_rejected_create_surfaces_momos_message()
    {
        var gateway = Gateway(new FakeMomo("""{"resultCode":22,"message":"Số tiền giao dịch không hợp lệ."}"""));

        var error = await Assert.ThrowsAsync<StreetBiz.Application.Common.Exceptions.DomainRuleException>(() =>
            gateway.CreateCheckoutAsync(new PaymentGatewayCheckoutRequest(1, "X", 1, "k", "MOMO", 10m), default));
        Assert.Contains("Số tiền giao dịch không hợp lệ", error.Message);
    }

    [Fact]
    public async Task A_genuine_ipn_is_accepted_and_a_tampered_one_is_not()
    {
        var gateway = Gateway(new FakeMomo("{}"));
        var ipn = Ipn(amount: 50_000, resultCode: 0);

        var genuine = await gateway.VerifyCallbackAsync("MOMO", ipn, null, default);
        var tampered = await gateway.VerifyCallbackAsync("MOMO", ipn.Replace("50000", "1000"), null, default);

        Assert.True(genuine.SignatureValid);
        Assert.Equal("SUCCESS", genuine.Status);
        Assert.Equal("SB-T42", genuine.ProviderReference);
        Assert.Equal(50_000m, genuine.Amount);
        Assert.False(tampered.SignatureValid);
    }

    [Theory]
    [InlineData(0, "SUCCESS")]
    [InlineData(1006, "FAILED")] // buyer declined
    [InlineData(1005, "FAILED")] // link expired
    [InlineData(1000, null)] // waiting for the buyer
    [InlineData(99, null)] // MoMo-side error: must not cancel an order that may be paid
    [InlineData(42, null)]
    public async Task Query_maps_only_final_result_codes_to_a_status(int resultCode, string? expected)
    {
        var gateway = Gateway(new FakeMomo(
            $$"""{"partnerCode":"MOMO","orderId":"SB-T42","amount":50000,"resultCode":{{resultCode}},"message":"x"}"""));

        var status = await gateway.QueryPaymentAsync("MOMO", "SB-T42", default);

        Assert.NotNull(status);
        Assert.Equal(expected, status!.Status);
        Assert.True(status.SignatureValid);
    }

    [Fact]
    public async Task Without_momo_credentials_the_sandbox_link_is_kept()
    {
        var gateway = new ConfiguredPaymentGateway(
            Options.Create(new PaymentGatewaySettings { SandboxEnabled = true }), new FakeFactory(new FakeMomo("{}")));

        var result = await gateway.CreateCheckoutAsync(
            new PaymentGatewayCheckoutRequest(19, "SB-000019", 42, "k", "MOMO", 50_000m), default);

        Assert.StartsWith("streetbiz://payment/sandbox/momo", result.PaymentUrl);
        Assert.Null(await gateway.QueryPaymentAsync("MOMO", "SB-T42", default));
    }

    private static ConfiguredPaymentGateway Gateway(FakeMomo momo) =>
        new(Options.Create(new PaymentGatewaySettings
        {
            Momo = new PaymentProviderSettings
            {
                ApiBaseUrl = "https://test-payment.momo.vn/v2/gateway/api",
                PartnerCode = "MOMO",
                AccessKey = AccessKey,
                SecretKey = SecretKey,
                ReturnUrl = "http://localhost:5173/customer/orders/{referenceId}/payment",
                NotifyUrl = "http://localhost:5023/api/payments/momo/callback",
            },
        }), new FakeFactory(momo));

    private static string Ipn(long amount, int resultCode)
    {
        const string orderInfo = "StreetBiz SB-000019";
        var raw = $"accessKey={AccessKey}&amount={amount}&extraData=&message=Thành công.&orderId=SB-T42" +
            $"&orderInfo={orderInfo}&orderType=momo_wallet&partnerCode=MOMO&payType=qr&requestId=r1" +
            $"&responseTime=1790000000000&resultCode={resultCode}&transId=4088878653";
        return JsonSerializer.Serialize(new
        {
            partnerCode = "MOMO", orderId = "SB-T42", requestId = "r1", amount, orderInfo,
            orderType = "momo_wallet", transId = 4088878653L, resultCode, message = "Thành công.",
            payType = "qr", responseTime = 1790000000000L, extraData = "", signature = Sign(raw),
        }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    private static string Sign(string raw)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(SecretKey));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

    private sealed class FakeMomo(string responseJson) : HttpMessageHandler
    {
        public string? LastUri { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUri = request.RequestUri!.ToString();
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
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
