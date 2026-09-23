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
    IOptions<PaymentGatewaySettings> options) : IPaymentGateway
{
    private readonly PaymentGatewaySettings settings = options.Value;

    public Task<PaymentGatewayCheckoutResult> CreateCheckoutAsync(
        PaymentGatewayCheckoutRequest request,
        CancellationToken cancellationToken)
    {
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
        string? signature,
        CancellationToken cancellationToken)
    {
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
