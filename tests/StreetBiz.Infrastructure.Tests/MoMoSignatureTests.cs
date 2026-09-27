using System.Security.Cryptography;
using System.Text;
using StreetBiz.Infrastructure.Payments;

namespace StreetBiz.Infrastructure.Tests;

/// <summary>
/// Pins the exact field order MoMo's AIO v2 API requires (alphabetical by key, joined by '&amp;').
/// A wrong order still produces a hex string — MoMo just rejects it as an invalid signature with
/// no further detail — so this is the one place that order is written down and checked.
/// </summary>
public sealed class MoMoSignatureTests
{
    private const string AccessKey = "F8BBA842ECF85";
    private const string SecretKey = "K951B6PE1waDMi640xX08PD3vg6EkVlz";

    [Fact]
    public void The_create_signature_uses_MoMos_documented_field_order()
    {
        var raw = MoMoSignature.ForCreate(
            accessKey: AccessKey,
            amount: "50000",
            extraData: "",
            ipnUrl: "https://api.example.com/api/payments/momo/callback",
            orderId: "SB-42",
            orderInfo: "StreetBiz - Ky 1/3",
            partnerCode: "MOMO",
            redirectUrl: "https://app.example.com/vendor/finance",
            requestId: "SB-42",
            requestType: "captureWallet");

        Assert.Equal(
            "accessKey=F8BBA842ECF85&amount=50000&extraData=&ipnUrl=https://api.example.com/api/payments/momo/callback" +
            "&orderId=SB-42&orderInfo=StreetBiz - Ky 1/3&partnerCode=MOMO" +
            "&redirectUrl=https://app.example.com/vendor/finance&requestId=SB-42&requestType=captureWallet",
            raw);
    }

    [Fact]
    public void The_ipn_signature_uses_a_different_field_set_than_create()
    {
        var raw = MoMoSignature.ForIpn(
            accessKey: AccessKey,
            amount: "50000",
            extraData: "",
            message: "Success",
            orderId: "SB-42",
            orderInfo: "StreetBiz - Ky 1/3",
            orderType: "momo_wallet",
            partnerCode: "MOMO",
            payType: "qr",
            requestId: "SB-42",
            responseTime: "1695600000000",
            resultCode: "0",
            transId: "9876543210");

        Assert.Equal(
            "accessKey=F8BBA842ECF85&amount=50000&extraData=&message=Success&orderId=SB-42" +
            "&orderInfo=StreetBiz - Ky 1/3&orderType=momo_wallet&partnerCode=MOMO&payType=qr" +
            "&requestId=SB-42&responseTime=1695600000000&resultCode=0&transId=9876543210",
            raw);
    }

    [Fact]
    public void Sign_matches_a_plain_HMACSHA256_hex_digest_computed_independently()
    {
        const string raw = "accessKey=F8BBA842ECF85&amount=50000";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(SecretKey));
        var expected = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();

        Assert.Equal(expected, MoMoSignature.Sign(raw, SecretKey));
    }

    [Fact]
    public void Matches_accepts_the_correct_signature_and_rejects_everything_else()
    {
        const string raw = "accessKey=F8BBA842ECF85&amount=50000";
        var signature = MoMoSignature.Sign(raw, SecretKey);

        Assert.True(MoMoSignature.Matches(raw, SecretKey, signature));
        Assert.True(MoMoSignature.Matches(raw, SecretKey, signature.ToUpperInvariant()));
        Assert.False(MoMoSignature.Matches(raw, SecretKey, "00"));
        Assert.False(MoMoSignature.Matches(raw, SecretKey, null));
        Assert.False(MoMoSignature.Matches(raw, SecretKey, "not-hex-at-all"));
        Assert.False(MoMoSignature.Matches(raw, "a-different-secret", signature));
    }
}
