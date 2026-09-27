using System.Text.Json.Serialization;

namespace StreetBiz.Infrastructure.Payments;

/// <summary>MoMo's "captureWallet" create-payment request body.</summary>
public sealed record MoMoCreateRequest(
    [property: JsonPropertyName("partnerCode")] string PartnerCode,
    [property: JsonPropertyName("partnerName")] string PartnerName,
    [property: JsonPropertyName("storeId")] string StoreId,
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("amount")] long Amount,
    [property: JsonPropertyName("orderId")] string OrderId,
    [property: JsonPropertyName("orderInfo")] string OrderInfo,
    [property: JsonPropertyName("redirectUrl")] string RedirectUrl,
    [property: JsonPropertyName("ipnUrl")] string IpnUrl,
    [property: JsonPropertyName("lang")] string Lang,
    [property: JsonPropertyName("extraData")] string ExtraData,
    [property: JsonPropertyName("requestType")] string RequestType,
    [property: JsonPropertyName("signature")] string Signature);

/// <summary>MoMo's response to a create-payment request. resultCode 0 = success.</summary>
public sealed record MoMoCreateResponse(
    [property: JsonPropertyName("partnerCode")] string? PartnerCode,
    [property: JsonPropertyName("orderId")] string? OrderId,
    [property: JsonPropertyName("requestId")] string? RequestId,
    [property: JsonPropertyName("resultCode")] int ResultCode,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("payUrl")] string? PayUrl,
    [property: JsonPropertyName("deeplink")] string? Deeplink,
    [property: JsonPropertyName("qrCodeUrl")] string? QrCodeUrl);

/// <summary>
/// MoMo's IPN (Instant Payment Notification): the server-to-server POST that is the only
/// trustworthy source of "did this actually get paid" — the browser redirect back to
/// <c>redirectUrl</c> carries similar fields but is never treated as confirmation on its own.
/// </summary>
public sealed record MoMoIpnPayload(
    [property: JsonPropertyName("partnerCode")] string? PartnerCode,
    [property: JsonPropertyName("orderId")] string? OrderId,
    [property: JsonPropertyName("requestId")] string? RequestId,
    [property: JsonPropertyName("amount")] long Amount,
    [property: JsonPropertyName("orderInfo")] string? OrderInfo,
    [property: JsonPropertyName("orderType")] string? OrderType,
    [property: JsonPropertyName("transId")] long TransId,
    [property: JsonPropertyName("resultCode")] int ResultCode,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("payType")] string? PayType,
    [property: JsonPropertyName("responseTime")] long ResponseTime,
    [property: JsonPropertyName("extraData")] string? ExtraData,
    [property: JsonPropertyName("signature")] string? Signature);
