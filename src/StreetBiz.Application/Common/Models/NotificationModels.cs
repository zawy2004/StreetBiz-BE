namespace StreetBiz.Application.Common.Models;

/// <summary>One in-app notification, as stored for its recipient.</summary>
public sealed record NotificationRow(
    long NotificationId,
    string Type,
    string Title,
    string Body,
    string? RelatedEntityType,
    long? RelatedEntityId,
    bool IsRead,
    DateTime SentAt);

/// <summary>One page of the inbox, newest first, plus whether older notifications remain.</summary>
public sealed record NotificationPage(IReadOnlyList<NotificationRow> Items, bool HasMore);
