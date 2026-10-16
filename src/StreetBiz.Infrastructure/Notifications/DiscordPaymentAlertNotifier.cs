using System.Globalization;
using System.Net.Http.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StreetBiz.Application.Common.Interfaces;

namespace StreetBiz.Infrastructure.Notifications;

/// <summary>Where payment alerts go. Leave the URL empty to switch alerts off.</summary>
public sealed class AlertSettings
{
    public const string SectionName = "Alerts";

    /// <summary>
    /// A Discord channel webhook (Server Settings → Integrations → Webhooks → New Webhook → Copy
    /// Webhook URL). It lets anyone post to the channel: keep it in user-secrets or an environment
    /// variable, never in a committed appsettings file.
    /// </summary>
    public string? DiscordWebhookUrl { get; set; }

    /// <summary>Optional text before each alert, e.g. "&lt;@&amp;123456789&gt;" to ping a role.</summary>
    public string? DiscordMention { get; set; }
}

/// <summary>
/// Posts a payment that needs a person to a Discord channel as one embed. Delivery is best
/// effort: a slow or failing Discord is logged and never fails the payment callback.
/// </summary>
public sealed class DiscordPaymentAlertNotifier(
    HttpClient http,
    IOptions<AlertSettings> options,
    IHostEnvironment environment,
    TimeProvider clock,
    ILogger<DiscordPaymentAlertNotifier> logger) : IPaymentAlertNotifier
{
    private static readonly CultureInfo Vietnamese = CultureInfo.GetCultureInfo("vi-VN");

    public async Task NotifyAsync(PaymentAlert alert, CancellationToken cancellationToken)
    {
        var url = options.Value.DiscordWebhookUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            using var response = await http.PostAsJsonAsync(url, Message(alert), cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Discord rejected a payment alert ({StatusCode}) for {Provider} {Outcome} {Reference}.",
                    (int)response.StatusCode, alert.Provider, alert.Outcome, alert.ProviderReference);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(exception,
                "Could not send a payment alert to Discord for {Provider} {Outcome} {Reference}.",
                alert.Provider, alert.Outcome, alert.ProviderReference);
        }
    }

    /// <summary>The Discord webhook body: an optional mention and one embed with the facts a person needs.</summary>
    private object Message(PaymentAlert alert) => new
    {
        username = "StreetBiz Payments",
        content = string.IsNullOrWhiteSpace(options.Value.DiscordMention) ? null : options.Value.DiscordMention,
        embeds = new[]
        {
            new
            {
                title = $"Thanh toán cần xử lý: {alert.Provider} · {alert.Outcome}",
                description = alert.Reason,
                color = alert.Outcome == "ERROR" || alert.Outcome == "REJECTED" ? 0xB42318 : 0xF59E0B,
                fields = new[]
                {
                    Field("Mã thanh toán", alert.ProviderReference),
                    Field("Số tiền", alert.Amount is { } amount ? amount.ToString("N0", Vietnamese) + " đ" : null),
                    Field("Mã GD nhà cung cấp", alert.ProviderEventId),
                    Field("Giao dịch StreetBiz", alert.TransactionId is { } id ? $"#{id}" : null),
                    Field("Callback event", alert.CallbackEventId is { } eventId and > 0 ? $"#{eventId}" : null),
                    Field("Môi trường", environment.EnvironmentName),
                },
                timestamp = clock.GetUtcNow().ToString("o", CultureInfo.InvariantCulture),
            },
        },
    };

    // Discord refuses an embed field with an empty value.
    private static object Field(string name, string? value) =>
        new { name, value = string.IsNullOrWhiteSpace(value) ? "—" : value, inline = true };
}
