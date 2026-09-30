using StreetBiz.Application.Common.Models;

namespace StreetBiz.Application.Common.Interfaces;

/// <summary>
/// Reads the Notifications rows that the workflows (orders, fees, reviews, food safety...)
/// write for a user. Every method is scoped to one recipient: a notification of somebody
/// else behaves exactly like one that does not exist.
/// </summary>
public interface INotificationRepository
{
    /// <summary>
    /// Newest first. `beforeNotificationId` null starts at the newest notification,
    /// otherwise the page ends just before that id.
    /// </summary>
    Task<NotificationPage> ListAsync(
        long userId,
        long? beforeNotificationId,
        int take,
        CancellationToken cancellationToken);

    Task<int> CountUnreadAsync(long userId, CancellationToken cancellationToken);

    /// <summary>False when the notification does not exist or belongs to somebody else.</summary>
    Task<bool> MarkReadAsync(long userId, long notificationId, CancellationToken cancellationToken);

    /// <summary>Returns how many rows changed.</summary>
    Task<int> MarkAllReadAsync(long userId, CancellationToken cancellationToken);
}
