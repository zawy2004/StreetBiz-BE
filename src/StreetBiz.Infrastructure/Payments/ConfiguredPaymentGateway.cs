using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.Infrastructure.Payments;

public sealed class ConfiguredPaymentGateway(
    IOptions<PaymentGatewaySettings> options,
    HttpClient httpClient) : IPaymentGateway
{
    private readonly PaymentGatewaySettings settings = options.Value;

    public async Task<PaymentGatewayCheckoutResult> CreateCheckoutAsync(
        PaymentGatewayCheckoutRequest request,
        CancellationToken cancellationToken)
    {
        var provider = settings.For(request.Provider);

        if (string.Equals(request.Provider, PaymentProviders.Momo, StringComparison.Ordinal) && provider.HasRealCredentials)
        {
            return await CreateMomoCheckoutAsync(provider, request, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(provider.CheckoutUrlTemplate))
        {
            var url = provider.CheckoutUrlTemplate
                .Replace("{referenceId}", request.ReferenceId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{referenceCode}", Uri.EscapeDataString(request.ReferenceCode), StringComparison.Ordinal)
                .Replace("{transactionId}", request.TransactionId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{idempotencyKey}", Uri.EscapeDataString(request.IdempotencyKey), StringComparison.Ordinal)
                .Replace("{amount}", request.Amount.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
            return new PaymentGatewayCheckoutResult(url, null);
        }

        if (settings.SandboxEnabled)
        {
            var url = $"streetbiz://payment/sandbox/{request.Provider.ToLowerInvariant()}" +
                $"?referenceId={request.ReferenceId}&transactionId={request.TransactionId}";
            return new PaymentGatewayCheckoutResult(url, null);
        }

        throw new DomainRuleException(
            $"Payment provider {request.Provider} is not configured.");
    }

    /// <summary>
    /// MoMo's "captureWallet" create-payment call. MoMo rejects any orderId it has seen before
    /// (resultCode 41), including a retry of the same still-pending order, and orderIds are unique
    /// per partner code — shared by every developer using MoMo's test key. So each call gets a
    /// fresh orderId, and our Idempotency-Key rides along in the signed extraData: MoMo echoes it
    /// back in the IPN, which is how a payment made through an earlier attempt's payUrl still
    /// matches its transaction after a retry has replaced provider_reference with the new orderId.
    /// </summary>
    private async Task<PaymentGatewayCheckoutResult> CreateMomoCheckoutAsync(
        PaymentProviderSettings momo,
        PaymentGatewayCheckoutRequest request,
        CancellationToken cancellationToken)
    {
        var orderId = $"SB-{request.TransactionId}-{Guid.NewGuid().ToString("N")[..8]}";
        var amount = (long)request.Amount;
        var amountText = amount.ToString(CultureInfo.InvariantCulture);
        var extraData = MoMoExtraData.Encode(request.IdempotencyKey);
        const string requestType = "captureWallet";
        var orderInfo = $"StreetBiz - {request.ReferenceCode}";
        var redirectUrl = momo.RedirectUrl ?? "";
        var ipnUrl = momo.IpnUrl ?? "";

        var rawSignature = MoMoSignature.ForCreate(
            momo.AccessKey!, amountText, extraData, ipnUrl, orderId, orderInfo,
            momo.PartnerCode!, redirectUrl, orderId, requestType);
        var signature = MoMoSignature.Sign(rawSignature, momo.SecretKey!);

        var payload = new MoMoCreateRequest(
            momo.PartnerCode!, "StreetBiz", "StreetBizVendor", orderId, amount, orderId,
            orderInfo, redirectUrl, ipnUrl, "vi", extraData, requestType, signature);

        MoMoCreateResponse? body;
        try
        {
            using var response = await httpClient.PostAsJsonAsync(momo.ApiEndpoint, payload, cancellationToken);
            body = await response.Content.ReadFromJsonAsync<MoMoCreateResponse>(cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException)
        {
            // Network failure, or MoMo answered with something that is not the JSON it documents
            // (outage, wrong ApiEndpoint) — either way this is a clean refusal, not a raw 500.
            throw new DomainRuleException("Không kết nối được với MoMo. Vui lòng thử lại.");
        }

        if (body is null || body.ResultCode != 0 || string.IsNullOrWhiteSpace(body.PayUrl))
        {
            throw new DomainRuleException(
                $"MoMo từ chối yêu cầu thanh toán: {body?.Message ?? "không nhận được phản hồi"} (resultCode={body?.ResultCode.ToString(CultureInfo.InvariantCulture) ?? "?"}).");
        }

        // Stored as provider_reference (replacing an earlier attempt's): the IPN for this attempt
        // matches on it; an IPN for an earlier attempt falls back to the idempotency key.
        return new PaymentGatewayCheckoutResult(body.PayUrl, orderId);
    }

    public Task<PaymentGatewayCallback> VerifyCallbackAsync(
        string provider,
        string rawPayload,
        string? signature,
        CancellationToken cancellationToken)
    {
        var providerSettings = settings.For(provider);
        if (string.Equals(provider, PaymentProviders.Momo, StringComparison.Ordinal) && providerSettings.HasRealCredentials)
        {
            return Task.FromResult(VerifyMomoIpn(rawPayload, providerSettings));
        }

        string? providerReference = null;
        string? idempotencyKey = null;
        decimal? amount = null;
        string? status = null;
        try
        {
            using var json = JsonDocument.Parse(rawPayload);
            var root = json.RootElement;
            providerReference = Text(root, "providerReference");
            idempotencyKey = Text(root, "idempotencyKey");
            status = Text(root, "status")?.Trim().ToUpperInvariant();
            if (root.TryGetProperty("amount", out var amountElement)
                && amountElement.TryGetDecimal(out var parsedAmount))
            {
                amount = parsedAmount;
            }
            if (providerReference?.Length > 100)
            {
                providerReference = providerReference[..100];
                return Task.FromResult(new PaymentGatewayCallback(
                    providerReference, idempotencyKey, amount, status, false));
            }
        }
        catch (JsonException)
        {
            return Task.FromResult(new PaymentGatewayCallback(
                providerReference, idempotencyKey, amount, status, false));
        }

        var secret = settings.For(provider).CallbackSecret;
        var valid = !string.IsNullOrWhiteSpace(secret)
            && !string.IsNullOrWhiteSpace(signature)
            && VerifyHmac(rawPayload, signature, secret);
        return Task.FromResult(new PaymentGatewayCallback(
            providerReference, idempotencyKey, amount, status, valid));
    }

    /// <summary>
    /// MoMo's IPN carries its own signature inside the JSON body (not a header, unlike the
    /// generic path above), over a different field set than the create-payment call. The
    /// browser's redirect back to <c>redirectUrl</c> carries similar-looking fields but is never
    /// what reaches here — MoMo POSTs the IPN to <c>ipnUrl</c> independently of the redirect.
    /// </summary>
    private static PaymentGatewayCallback VerifyMomoIpn(string rawPayload, PaymentProviderSettings momo)
    {
        MoMoIpnPayload? ipn;
        try
        {
            ipn = JsonSerializer.Deserialize<MoMoIpnPayload>(rawPayload);
        }
        catch (JsonException)
        {
            return new PaymentGatewayCallback(null, null, null, null, false);
        }

        if (ipn is null)
        {
            return new PaymentGatewayCallback(null, null, null, null, false);
        }

        var rawSignature = MoMoSignature.ForIpn(
            momo.AccessKey!,
            ipn.Amount.ToString(CultureInfo.InvariantCulture),
            ipn.ExtraData ?? "",
            ipn.Message ?? "",
            ipn.OrderId ?? "",
            ipn.OrderInfo ?? "",
            ipn.OrderType ?? "",
            ipn.PartnerCode ?? "",
            ipn.PayType ?? "",
            ipn.RequestId ?? "",
            ipn.ResponseTime.ToString(CultureInfo.InvariantCulture),
            ipn.ResultCode.ToString(CultureInfo.InvariantCulture),
            ipn.TransId.ToString(CultureInfo.InvariantCulture));
        var valid = MoMoSignature.Matches(rawSignature, momo.SecretKey!, ipn.Signature);

        return new PaymentGatewayCallback(
            ipn.OrderId,
            MoMoExtraData.DecodeIdempotencyKey(ipn.ExtraData),
            ipn.Amount,
            ipn.ResultCode == 0 ? PaymentStatuses.Success : PaymentStatuses.Failed,
            valid);
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool VerifyHmac(string payload, string supplied, string secret)
    {
        var normalized = supplied.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase)
            ? supplied[7..]
            : supplied;
        byte[] suppliedBytes;
        try
        {
            suppliedBytes = Convert.FromHexString(normalized);
        }
        catch (FormatException)
        {
            return false;
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var expected = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return suppliedBytes.Length == expected.Length
            && CryptographicOperations.FixedTimeEquals(suppliedBytes, expected);
    }
}

public sealed class ConfiguredRefundGateway : IRefundGateway
{
    public Task<RefundGatewayResult> RequestRefundAsync(
        RefundGatewayRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult(new RefundGatewayResult(false, null));
}
