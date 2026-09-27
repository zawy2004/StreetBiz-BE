using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Infrastructure.Payments;

namespace StreetBiz.Infrastructure.Tests;

/// <summary>
/// What ConfiguredPaymentGateway actually sends to MoMo and how it reads MoMo's IPN, with MoMo
/// replaced by a fake HTTP handler. The behaviours pinned here were checked against MoMo's real
/// test endpoint: a repeated orderId is refused (resultCode 41), and extraData must be base64.
/// </summary>
public sealed class MoMoGatewayTests
{
    private const string AccessKey = "test-access-key";
    private const string SecretKey = "test-secret-key";

    private static readonly PaymentGatewayCheckoutRequest Checkout =
        new(3, "Kỳ 3/3 · Tháng 10/2026", 16, "3f2b8c1e-5d7a-4c2e-9b1f-0a6e4d8c2b71", PaymentProviders.Momo, 1_040_000m);

    [Fact]
    public async Task Each_call_gets_a_fresh_orderId_because_MoMo_refuses_a_repeated_one()
    {
        var momo = new FakeMoMo();
        var gateway = Gateway(momo);

        var first = await gateway.CreateCheckoutAsync(Checkout, default);
        var retry = await gateway.CreateCheckoutAsync(Checkout, default);

        Assert.StartsWith("SB-16-", first.ProviderReference);
        Assert.StartsWith("SB-16-", retry.ProviderReference);
        Assert.NotEqual(first.ProviderReference, retry.ProviderReference);
        Assert.Equal("https://test-payment.momo.vn/pay/1", first.PaymentUrl);
    }

    [Fact]
    public async Task The_request_is_signed_over_exactly_the_fields_it_sends()
    {
        var momo = new FakeMoMo();
        await Gateway(momo).CreateCheckoutAsync(Checkout, default);

        var sent = momo.Requests.Single();
        var raw = MoMoSignature.ForCreate(
            AccessKey, Text(sent, "amount"), Text(sent, "extraData"), Text(sent, "ipnUrl"),
            Text(sent, "orderId"), Text(sent, "orderInfo"), Text(sent, "partnerCode"),
            Text(sent, "redirectUrl"), Text(sent, "requestId"), Text(sent, "requestType"));

        Assert.Equal(MoMoSignature.Sign(raw, SecretKey), Text(sent, "signature"));
        Assert.Equal("1040000", Text(sent, "amount"));
        Assert.Equal("captureWallet", Text(sent, "requestType"));
        Assert.Equal(Checkout.IdempotencyKey, MoMoExtraData.DecodeIdempotencyKey(Text(sent, "extraData")));
    }

    [Fact]
    public async Task A_refusal_from_MoMo_is_a_clear_domain_error_not_a_server_error()
    {
        var refused = Gateway(new FakeMoMo(resultCode: 41, message: "Yêu cầu bị từ chối vì trùng orderId."));
        var outage = Gateway(new FakeMoMo(rawBody: "<html>502 Bad Gateway</html>"));

        var refusal = await Assert.ThrowsAsync<DomainRuleException>(() => refused.CreateCheckoutAsync(Checkout, default));
        Assert.Contains("trùng orderId", refusal.Message);
        await Assert.ThrowsAsync<DomainRuleException>(() => outage.CreateCheckoutAsync(Checkout, default));
    }

    [Fact]
    public async Task A_genuine_IPN_is_valid_and_carries_the_idempotency_key_back()
    {
        var ipn = SignedIpn(resultCode: 0, amount: 1_040_000);

        var callback = await Gateway(new FakeMoMo()).VerifyCallbackAsync(PaymentProviders.Momo, ipn, null, default);

        Assert.True(callback.SignatureValid);
        Assert.Equal("SB-16-abcd1234", callback.ProviderReference);
        Assert.Equal(Checkout.IdempotencyKey, callback.IdempotencyKey);
        Assert.Equal(1_040_000m, callback.Amount);
        Assert.Equal(PaymentStatuses.Success, callback.Status);
    }

    [Fact]
    public async Task A_tampered_IPN_is_invalid_and_a_cancelled_payment_is_a_failure()
    {
        var tampered = SignedIpn(resultCode: 0, amount: 1_040_000).Replace("1040000", "1", StringComparison.Ordinal);
        var cancelled = SignedIpn(resultCode: 1006, amount: 1_040_000);
        var gateway = Gateway(new FakeMoMo());

        Assert.False((await gateway.VerifyCallbackAsync(PaymentProviders.Momo, tampered, null, default)).SignatureValid);
        var failed = await gateway.VerifyCallbackAsync(PaymentProviders.Momo, cancelled, null, default);
        Assert.True(failed.SignatureValid);
        Assert.Equal(PaymentStatuses.Failed, failed.Status);
    }

    private static ConfiguredPaymentGateway Gateway(FakeMoMo momo) => new(
        Options.Create(new PaymentGatewaySettings
        {
            Momo = new PaymentProviderSettings
            {
                PartnerCode = "MOMO",
                AccessKey = AccessKey,
                SecretKey = SecretKey,
                ApiEndpoint = "https://test-payment.momo.vn/v2/gateway/api/create",
                RedirectUrl = "http://localhost:5173/vendor/finance",
                IpnUrl = "https://example.test/api/payments/momo/callback",
            },
        }),
        new HttpClient(momo));

    private static string SignedIpn(int resultCode, long amount)
    {
        const string orderId = "SB-16-abcd1234";
        var extraData = MoMoExtraData.Encode(Checkout.IdempotencyKey);
        var raw = MoMoSignature.ForIpn(
            AccessKey, amount.ToString(), extraData, "Thành công.", orderId, "StreetBiz - Kỳ 3/3",
            "momo_wallet", "MOMO", "qr", orderId, "1790498064571", resultCode.ToString(), "4088878653");
        return JsonSerializer.Serialize(new
        {
            partnerCode = "MOMO", orderId, requestId = orderId, amount, orderInfo = "StreetBiz - Kỳ 3/3",
            orderType = "momo_wallet", transId = 4088878653L, resultCode, message = "Thành công.", payType = "qr",
            responseTime = 1790498064571L, extraData, signature = MoMoSignature.Sign(raw, SecretKey),
        });
    }

    private static string Text(JsonElement json, string name) =>
        json.GetProperty(name).ValueKind == JsonValueKind.Number
            ? json.GetProperty(name).GetRawText()
            : json.GetProperty(name).GetString()!;

    private sealed class FakeMoMo(int resultCode = 0, string message = "Thành công.", string? rawBody = null)
        : HttpMessageHandler
    {
        private int calls;

        public List<JsonElement> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement.Clone());
            calls++;
            var body = rawBody ?? JsonSerializer.Serialize(new
            {
                partnerCode = "MOMO", resultCode, message,
                payUrl = resultCode == 0 ? $"https://test-payment.momo.vn/pay/{calls}" : null,
            });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
