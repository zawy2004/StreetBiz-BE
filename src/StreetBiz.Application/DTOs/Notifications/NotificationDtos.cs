namespace StreetBiz.Application.DTOs.Notifications;

public sealed record NotificationDto(
    long NotificationId,
    string Type,
    string Title,
    string Body,
    string? RelatedEntityType,
    long? RelatedEntityId,
    bool IsRead,
    DateTime SentAt);

public sealed record NotificationPageDto(IReadOnlyList<NotificationDto> Items, bool HasMore);

public sealed record NotificationUnreadCountDto(int UnreadCount);
