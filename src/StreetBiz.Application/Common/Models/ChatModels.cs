namespace StreetBiz.Application.Common.Models;

/// <summary>One thread as it appears in either side's inbox list.</summary>
public sealed record ChatConversationRow(
    long ConversationId,
    long StorefrontId,
    string StorefrontName,
    string? StorefrontImageUrl,
    long CustomerUserId,
    string CustomerName,
    DateTime CreatedAt,
    DateTime? LastMessageAt,
    string? LastMessageBody,
    long? LastMessageSenderUserId,
    int UnreadCount);

public sealed record ChatMessageRow(
    long MessageId,
    long ConversationId,
    long SenderUserId,
    string SenderName,
    string Body,
    DateTime SentAt,
    DateTime? ReadAt);

/// <summary>One page of a thread, oldest first, plus whether older messages remain.</summary>
public sealed record ChatMessagePage(IReadOnlyList<ChatMessageRow> Messages, bool HasMore);

/// <summary>
/// Who is looking at a thread. Both sides read and write the same rows, so every
/// repository call takes this instead of a bare user id: it decides which threads
/// are visible, which messages count as unread, and who may post.
/// </summary>
public sealed record ChatParticipant(long UserId, ChatParticipantSide Side);

/// <summary>The two accounts in a thread: the buyer, and the seller who owns the storefront.</summary>
public sealed record ChatThreadUsers(long CustomerUserId, long VendorUserId);

public enum ChatParticipantSide
{
    Customer,
    Vendor,
}
