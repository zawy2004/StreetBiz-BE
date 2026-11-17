using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace StreetBiz.Infrastructure.Notifications;

/// <summary>
/// SpeedSMS.vn adapter (POST /sms/send). The access token travels in an HTTP Basic header
/// ("token:x"), never in the URL or body, so it cannot reach request logs; only SpeedSMS's result
/// code is logged, which says what to fix (007 IP locked, 300 balance, 105 phone, ...).
/// status "success" means SpeedSMS accepted the request; it is not a delivery receipt.
/// </summary>
public sealed class SpeedSmsSender(
    HttpClient httpClient,
    IOptions<SmsSettings> options,
    ILogger<SpeedSmsSender> logger) : ISmsSender
{
    public const string DefaultEndpoint = "https://api.speedsms.vn/index.php/sms/send";

    public async Task SendAsync(string phoneNumber, string message, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var endpoint = string.IsNullOrWhiteSpace(settings.Endpoint) ? DefaultEndpoint : settings.Endpoint;

        // sms_type: 2 customer care (random number), 3 brandname, 4 default brandname (Notify),
        // 5 own Android phone, 6 fixed number. Only 3 and 5 take a sender.
        var smsType = int.TryParse(settings.SmsType, out var parsed) ? parsed : 2;
        var body = new Dictionary<string, object>
        {
            ["to"] = new[] { phoneNumber },
            ["content"] = message,
            ["sms_type"] = smsType,
            ["sender"] = smsType is 3 or 5 ? settings.Sender : string.Empty,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.ApiKey}:x")));

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = ReadResult(json);
        if (!result.Accepted)
        {
            logger.LogWarning("SpeedSMS refused an OTP message (sms_type {SmsType}): code {Code} {Error}", smsType, result.Code ?? "?", result.Error);
            throw new InvalidOperationException($"SpeedSMS refused the message (code {result.Code ?? "unknown"}).");
        }
    }

    private static (bool Accepted, string? Code, string? Error) ReadResult(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var code = root.TryGetProperty("code", out var c) ? c.ToString() : null;
            var error = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            var success = root.TryGetProperty("status", out var s) && s.GetString() == "success";

            // The one recipient being listed as invalid means nothing was sent, whatever the status says.
            if (success && root.TryGetProperty("data", out var data)
                && data.TryGetProperty("invalidPhone", out var invalid)
                && invalid.ValueKind == JsonValueKind.Array && invalid.GetArrayLength() > 0)
            {
                return (false, "105", "phone number rejected");
            }

            return (success, code, error);
        }
        catch (JsonException)
        {
            return (false, null, "unreadable response");
        }
    }
}
