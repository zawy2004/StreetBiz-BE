using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace StreetBiz.Infrastructure.Notifications;

/// <summary>Generic JSON-over-HTTP SMS gateway adapter; the message body is never logged.</summary>
public sealed class HttpSmsSender(HttpClient httpClient, IOptions<SmsSettings> options) : ISmsSender
{
    public async Task SendAsync(string phoneNumber, string message, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        using var request = new HttpRequestMessage(HttpMethod.Post, settings.Endpoint)
        {
            Content = JsonContent.Create(new { to = phoneNumber, from = settings.Sender, message }),
        };
        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        }

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
