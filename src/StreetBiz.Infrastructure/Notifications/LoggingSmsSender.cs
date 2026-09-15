using Microsoft.Extensions.Logging;

namespace StreetBiz.Infrastructure.Notifications;

/// <summary>Development SMS sender: logs the message instead of calling a provider (AS-09).</summary>
public sealed class LoggingSmsSender(ILogger<LoggingSmsSender> logger) : ISmsSender
{
    public Task SendAsync(string phoneNumber, string message, CancellationToken cancellationToken)
    {
        logger.LogInformation("[DEV-SMS] To {Phone}: {Message}", phoneNumber, message);
        return Task.CompletedTask;
    }
}
