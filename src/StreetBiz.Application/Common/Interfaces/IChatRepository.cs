using StreetBiz.Application.Common.Models;

namespace StreetBiz.Application.Common.Interfaces;

public interface IChatRepository
{
    /// <summary>
    /// Threads the participant takes part in, most recently active first. A
    /// vendor sees every thread of every storefront they own.
    /// </summary>
    Task<IReadOnlyList<ChatConversationRow>> ListConversationsAsync(
        ChatParticipant participant,
        CancellationToken cancellationToken);

    /// <summary>
    /// Null when the thread does not exist or belongs to somebody else - callers
    /// must not distinguish the two, or they leak which storefronts a customer
    /// has written to.
    /// </summary>
    Task<ChatConversationRow?> GetConversationAsync(
        ChatParticipant participant,
        long conversationId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Opens the customer's thread with this storefront, or returns the existing
    /// one. Only a customer can open a thread: a vendor messaging first would be
    /// unsolicited contact.
    /// </summary>
    Task<ChatConversationRow?> StartConversationAsync(
        long customerUserId,
        long storefrontId,
        CancellationToken cancellationToken);

    /// <summary>
    /// One page of a thread, oldest first. Paging walks backwards in time:
    /// `beforeMessageId` null starts at the newest message, otherwise the page
    /// ends just before that id. `HasMore` reports whether anything older remains.
    /// </summary>
    Task<ChatMessagePage> ListMessagesAsync(
        ChatParticipant participant,
        long conversationId,
        int take,
        long? beforeMessageId,
        CancellationToken cancellationToken);

    Task<ChatMessageRow?> SendMessageAsync(
        ChatParticipant participant,
        long conversationId,
        string body,
        CancellationToken cancellationToken);

    /// <summary>Marks what the other side sent as read. Returns how many rows changed.</summary>
    Task<int> MarkReadAsync(
        ChatParticipant participant,
        long conversationId,
        CancellationToken cancellationToken);

    /// <summary>Unread messages across every thread, for the navigation badge.</summary>
    Task<int> CountUnreadAsync(
        ChatParticipant participant,
        CancellationToken cancellationToken);

    /// <summary>Who to tell about a new message in this thread, wherever they are in the app.</summary>
    Task<ChatThreadUsers?> GetThreadUsersAsync(long conversationId, CancellationToken cancellationToken);
}
