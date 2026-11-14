using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace StreetBiz.Infrastructure.Notifications;

/// <summary>
/// eSMS.vn adapter (POST SendMultipleMessage_V4_post_json). Credentials travel in the JSON body as
/// eSMS requires, so neither the body nor the key is ever logged -- only eSMS's result code, which
/// says what to fix (101 wrong key, 103 balance, 104 brandname, 140 IP not whitelisted, ...).
/// CodeResult "100" means eSMS accepted the request; it is not a delivery receipt.
/// </summary>
public sealed class EsmsSmsSender(
    HttpClient httpClient,
    IOptions<SmsSettings> options,
    ILogger<EsmsSmsSender> logger) : ISmsSender
{
    public const string DefaultEndpoint = "https://rest.esms.vn/MainService.svc/json/SendMultipleMessage_V4_post_json/";

    private const string Accepted = "100";

    public async Task SendAsync(string phoneNumber, string message, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var endpoint = string.IsNullOrWhiteSpace(settings.Endpoint) ? DefaultEndpoint : settings.Endpoint;

        // eSMS wants every value as a string, including the flags.
        var body = new Dictionary<string, string>
        {
            ["ApiKey"] = settings.ApiKey,
            ["SecretKey"] = settings.SecretKey,
            ["Phone"] = phoneNumber,
            ["Content"] = message,
            ["SmsType"] = settings.SmsType,
            ["IsUnicode"] = "0",
            ["Sandbox"] = settings.Sandbox ? "1" : "0",
        };
        // Type 8 (fixed number) has no brandname, and eSMS answers 104 if one is sent anyway, so a
        // leftover Sms:Brandname must not leak into it. Types 1/2 need a registered brandname.
        if (settings.SmsType != "8" && !string.IsNullOrWhiteSpace(settings.Brandname))
        {
            body["Brandname"] = settings.Brandname;
        }

        using var response = await httpClient.PostAsJsonAsync(endpoint, body, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var (code, error) = ReadResult(json);
        if (code != Accepted)
        {
            logger.LogWarning("eSMS refused an OTP message: CodeResult {Code} {Error}", code ?? "?", error);
            throw new InvalidOperationException($"eSMS refused the message (CodeResult {code ?? "unknown"}).");
        }
    }

    private static (string? Code, string? Error) ReadResult(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var code = root.TryGetProperty("CodeResult", out var c) ? c.GetString() : null;
            var error = root.TryGetProperty("ErrorMessage", out var e) ? e.GetString() : null;
            return (code, error);
        }
        catch (JsonException)
        {
            return (null, "unreadable response");
        }
    }
}
