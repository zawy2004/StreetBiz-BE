namespace StreetBiz.Infrastructure.Notifications;

/// <summary>Delivers SMS (OTP) messages. Swap the logging implementation for a real provider later.</summary>
public interface ISmsSender
{
    Task SendAsync(string phoneNumber, string message, CancellationToken cancellationToken);
}
