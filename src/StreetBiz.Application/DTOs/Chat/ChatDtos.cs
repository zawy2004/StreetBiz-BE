namespace StreetBiz.Application.DTOs.Chat;

public sealed record ChatConversationDto(
    long ConversationId,
    long StorefrontId,
    string StorefrontName,
    string? StorefrontImageUrl,
    long CustomerUserId,
    string CustomerName,
    // The thread title for whoever is asking: a customer sees the storefront,
    // a vendor sees the customer.
    string CounterpartName,
    DateTime CreatedAt,
    DateTime? LastMessageAt,
    string? LastMessagePreview,
    bool LastMessageFromMe,
    int UnreadCount);

public sealed record ChatMessageDto(
    long MessageId,
    long ConversationId,
    long SenderUserId,
    string SenderName,
    bool FromMe,
    string Body,
    DateTime SentAt,
    DateTime? ReadAt);

public sealed record ChatThreadDto(
    ChatConversationDto Conversation,
    IReadOnlyList<ChatMessageDto> Messages,
    // True when messages older than the first one returned still exist.
    bool HasMore);

/// <summary>An older page of a thread, fetched as the reader scrolls up.</summary>
public sealed record ChatMessagePageDto(
    IReadOnlyList<ChatMessageDto> Messages,
    bool HasMore);

public sealed record ChatUnreadCountDto(int UnreadCount);
