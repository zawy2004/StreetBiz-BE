using System.Globalization;
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
    IHttpClientFactory? httpClientFactory = null,
    TimeProvider? clock = null) : IPaymentGateway
{
    private readonly PaymentGatewaySettings settings = options.Value;
    private readonly TimeProvider time = clock ?? TimeProvider.System;

    /// <summary>Real MoMo v2 is used for MOMO whenever its credentials are configured.</summary>
    private bool UsesMomo(string provider) =>
        provider == "MOMO" && settings.Momo.IsMomoConfigured && httpClientFactory is not null;

    /// <summary>SePay bank transfers are used for SEPAY whenever the account and an authentication are configured.</summary>
    private bool UsesSePay(string provider) =>
        provider == PaymentProviders.SePay && settings.SePay.IsConfigured;

    public bool IsProviderAvailable(string provider) =>
        !string.IsNullOrWhiteSpace(settings.For(provider).CheckoutUrlTemplate)
        || UsesMomo(provider)
        || UsesSePay(provider)
        || settings.SandboxEnabled;

    public Task<PaymentGatewayCheckoutResult> CreateCheckoutAsync(
        PaymentGatewayCheckoutRequest request,
        CancellationToken cancellationToken)
    {
        if (UsesSePay(request.Provider))
        {
            return Task.FromResult(SePayGateway.Create(settings.SePay, request));
        }

        if (UsesMomo(request.Provider))
        {
            return MomoGateway.CreateAsync(
                httpClientFactory!.CreateClient(MomoGateway.HttpClientName),
                settings.Momo,
                request,
                cancellationToken);
        }

        var provider = settings.For(request.Provider);
        if (!string.IsNullOrWhiteSpace(provider.CheckoutUrlTemplate))
        {
            var url = provider.CheckoutUrlTemplate
                .Replace("{referenceId}", request.ReferenceId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{referenceCode}", Uri.EscapeDataString(request.ReferenceCode), StringComparison.Ordinal)
                .Replace("{transactionId}", request.TransactionId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{idempotencyKey}", Uri.EscapeDataString(request.IdempotencyKey), StringComparison.Ordinal)
                .Replace("{amount}", request.Amount.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
            return Task.FromResult(new PaymentGatewayCheckoutResult(url, null));
        }

        if (settings.SandboxEnabled)
        {
            var url = $"streetbiz://payment/sandbox/{request.Provider.ToLowerInvariant()}" +
                $"?referenceId={request.ReferenceId}&transactionId={request.TransactionId}";
            return Task.FromResult(new PaymentGatewayCheckoutResult(url, null));
        }

        throw new DomainRuleException(
            $"Payment provider {request.Provider} is not configured.");
    }

    public Task<PaymentGatewayCallback> VerifyCallbackAsync(
        string provider,
        string rawPayload,
        PaymentCallbackHeaders? headers,
        CancellationToken cancellationToken)
    {
        // SePay authenticates with its own headers and payload, whether or not it is configured
        // (unconfigured, nothing authenticates, so the callback is recorded as REJECTED).
        if (provider == PaymentProviders.SePay)
        {
            return Task.FromResult(SePayGateway.VerifyWebhook(settings.SePay, rawPayload, headers, time.GetUtcNow()));
        }

        var signature = headers?.Signature;

        // MoMo signs the IPN body itself (no signature header), in its own field layout.
        if (UsesMomo(provider) && MomoGateway.IsIpn(rawPayload))
        {
            return Task.FromResult(MomoGateway.VerifyIpn(settings.Momo, rawPayload));
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

    public async Task<PaymentGatewayCallback?> QueryPaymentAsync(
        string provider,
        string providerReference,
        CancellationToken cancellationToken)
    {
        if (UsesMomo(provider))
        {
            return await MomoGateway.QueryAsync(
                httpClientFactory!.CreateClient(MomoGateway.HttpClientName),
                settings.Momo,
                providerReference,
                cancellationToken);
        }

        // Without an API token SePay cannot be asked: the webhook alone settles the payment.
        return UsesSePay(provider) && !string.IsNullOrWhiteSpace(settings.SePay.ApiToken) && httpClientFactory is not null
            ? await SePayGateway.QueryAsync(
                httpClientFactory.CreateClient(SePayGateway.HttpClientName),
                settings.SePay,
                providerReference,
                cancellationToken)
            : null;
    }

    public BankTransferInstructions? DescribeBankTransfer(string provider, string providerReference, decimal amount) =>
        UsesSePay(provider) ? SePayGateway.Instructions(settings.SePay, providerReference, amount) : null;

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
