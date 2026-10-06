using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Persistence.Repositories;

public sealed class ChatRepository(
    StreetBizDbContext dbContext,
    TimeProvider clock) : IChatRepository
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    /*
      DATETIME2 carries no time zone, so EF materialises every stored timestamp as
      DateTimeKind.Unspecified and System.Text.Json then writes it without a "Z".
      A freshly written row still holds the Utc-kind value we just created and is
      serialised *with* one, so the same message would arrive at the browser as two
      different instants depending on whether it came from the write or a later
      read - seven hours apart in Vietnam. Everything in these tables is written as
      UTC, so stamping the kind back on after materialisation restores the truth.
    */
    private static DateTime Utc(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static DateTime? Utc(DateTime? value) =>
        value is null ? null : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);

    private static ChatConversationRow WithUtcTimes(ChatConversationRow row) => row with
    {
        CreatedAt = Utc(row.CreatedAt),
        LastMessageAt = Utc(row.LastMessageAt),
    };

    private static ChatMessageRow WithUtcTimes(ChatMessageRow row) => row with
    {
        SentAt = Utc(row.SentAt),
        ReadAt = Utc(row.ReadAt),
    };

    /// <summary>
    /// Every read goes through this, so "can this account see this thread" is
    /// answered in exactly one place. A customer owns their own threads; a
    /// vendor reaches a thread through the storefront's registration.
    /// </summary>
    private IQueryable<ChatConversation> Visible(ChatParticipant participant) =>
        participant.Side == ChatParticipantSide.Customer
            ? dbContext.ChatConversations.Where(c => c.customer_user_id == participant.UserId)
            : dbContext.ChatConversations.Where(c =>
                c.storefront.registration.vendor.user_id == participant.UserId);

    public async Task<IReadOnlyList<ChatConversationRow>> ListConversationsAsync(
        ChatParticipant participant,
        CancellationToken cancellationToken)
    {
        // Ordered on the entity, before projecting: ordering the projected record
        // instead asks SQL Server to sort by the correlated sub-selects that build
        // it, which EF cannot translate.
        var rows = await Project(
                Visible(participant)
                    .AsNoTracking()
                    .OrderByDescending(c => c.last_message_at ?? c.created_at)
                    .ThenByDescending(c => c.conversation_id),
                participant)
            .ToListAsync(cancellationToken);

        return rows.Select(WithUtcTimes).ToList();
    }

    public async Task<ChatConversationRow?> GetConversationAsync(
        ChatParticipant participant,
        long conversationId,
        CancellationToken cancellationToken)
    {
        var row = await Project(
                Visible(participant).AsNoTracking().Where(c => c.conversation_id == conversationId),
                participant)
            .SingleOrDefaultAsync(cancellationToken);
        return row is null ? null : WithUtcTimes(row);
    }

    public async Task<ChatConversationRow?> StartConversationAsync(
        long customerUserId,
        long storefrontId,
        CancellationToken cancellationToken)
    {
        var storefrontExists = await dbContext.Storefronts
            .AsNoTracking()
            .AnyAsync(s => s.storefront_id == storefrontId, cancellationToken);
        if (!storefrontExists) return null;

        var participant = new ChatParticipant(customerUserId, ChatParticipantSide.Customer);
        var existing = await dbContext.ChatConversations
            .AsNoTracking()
            .Where(c => c.storefront_id == storefrontId && c.customer_user_id == customerUserId)
            .Select(c => c.conversation_id)
            .SingleOrDefaultAsync(cancellationToken);

        if (existing == 0)
        {
            var conversation = new ChatConversation
            {
                storefront_id = storefrontId,
                customer_user_id = customerUserId,
                created_at = Now,
            };
            dbContext.ChatConversations.Add(conversation);
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                existing = conversation.conversation_id;
            }
            catch (DbUpdateException)
            {
                // UQ_ChatConversations_OnePerPair: the customer opened the same
                // storefront twice at once. Their other request won; reuse it.
                dbContext.ChangeTracker.Clear();
                existing = await dbContext.ChatConversations
                    .AsNoTracking()
                    .Where(c => c.storefront_id == storefrontId
                                && c.customer_user_id == customerUserId)
                    .Select(c => c.conversation_id)
                    .SingleAsync(cancellationToken);
            }
        }

        return await GetConversationAsync(participant, existing, cancellationToken);
    }

    public async Task<ChatMessagePage> ListMessagesAsync(
        ChatParticipant participant,
        long conversationId,
        int take,
        long? beforeMessageId,
        CancellationToken cancellationToken)
    {
        if (!await Visible(participant).AnyAsync(c => c.conversation_id == conversationId, cancellationToken))
        {
            return new ChatMessagePage([], false);
        }

        // Newest first here so paging can walk backwards; one extra row is read
        // to learn whether anything older exists without a second COUNT query.
        var newest = await dbContext.ChatMessages
            .AsNoTracking()
            .Where(m => m.conversation_id == conversationId
                        && (beforeMessageId == null || m.message_id < beforeMessageId))
            .OrderByDescending(m => m.message_id)
            .Take(take + 1)
            .Select(m => new ChatMessageRow(
                m.message_id,
                m.conversation_id,
                m.sender_user_id,
                m.sender_user.full_name,
                m.body,
                m.sent_at,
                m.read_at))
            .ToListAsync(cancellationToken);

        var hasMore = newest.Count > take;
        if (hasMore) newest.RemoveAt(newest.Count - 1);

        // Flipped so the caller gets oldest first and can append without re-sorting.
        newest.Reverse();
        return new ChatMessagePage(newest.Select(WithUtcTimes).ToList(), hasMore);
    }

    public async Task<ChatMessageRow?> SendMessageAsync(
        ChatParticipant participant,
        long conversationId,
        string body,
        CancellationToken cancellationToken)
    {
        var conversation = await Visible(participant)
            .SingleOrDefaultAsync(c => c.conversation_id == conversationId, cancellationToken);
        if (conversation is null) return null;

        var sentAt = Now;
        var message = new ChatMessage
        {
            conversation_id = conversationId,
            sender_user_id = participant.UserId,
            body = body,
            sent_at = sentAt,
        };
        dbContext.ChatMessages.Add(message);
        conversation.last_message_at = sentAt;
        await dbContext.SaveChangesAsync(cancellationToken);

        var senderName = await dbContext.UserAccounts
            .AsNoTracking()
            .Where(u => u.user_id == participant.UserId)
            .Select(u => u.full_name)
            .SingleAsync(cancellationToken);

        return new ChatMessageRow(
            message.message_id,
            conversationId,
            participant.UserId,
            senderName,
            body,
            sentAt,
            null);
    }

    public async Task<int> MarkReadAsync(
        ChatParticipant participant,
        long conversationId,
        CancellationToken cancellationToken)
    {
        if (!await Visible(participant).AnyAsync(c => c.conversation_id == conversationId, cancellationToken))
        {
            return 0;
        }

        DateTime? readAt = Now;
        return await dbContext.ChatMessages
            .Where(m => m.conversation_id == conversationId
                        && m.sender_user_id != participant.UserId
                        && m.read_at == null)
            .ExecuteUpdateAsync(
                set => set.SetProperty(m => m.read_at, readAt),
                cancellationToken);
    }

    public Task<int> CountUnreadAsync(
        ChatParticipant participant,
        CancellationToken cancellationToken) =>
        Visible(participant)
            .AsNoTracking()
            .SelectMany(c => c.ChatMessages)
            .CountAsync(
                m => m.sender_user_id != participant.UserId && m.read_at == null,
                cancellationToken);

    public Task<ChatThreadUsers?> GetThreadUsersAsync(
        long conversationId,
        CancellationToken cancellationToken) =>
        dbContext.ChatConversations
            .AsNoTracking()
            .Where(c => c.conversation_id == conversationId)
            .Select(c => new ChatThreadUsers(
                c.customer_user_id,
                c.storefront.registration.vendor.user_id))
            .SingleOrDefaultAsync(cancellationToken);

    private static IQueryable<ChatConversationRow> Project(
        IQueryable<ChatConversation> conversations,
        ChatParticipant participant) =>
        conversations.Select(c => new ChatConversationRow(
            c.conversation_id,
            c.storefront_id,
            c.storefront.storefront_name,
            c.storefront.image_url,
            c.customer_user_id,
            c.customer_user.full_name,
            c.created_at,
            c.last_message_at,
            // Ordered by id, like the paging query: identity order is insertion
            // order, and it lets IX_ChatMessages_Thread answer both.
            c.ChatMessages
                .OrderByDescending(m => m.message_id)
                .Select(m => m.body)
                .FirstOrDefault(),
            c.ChatMessages
                .OrderByDescending(m => m.message_id)
                .Select(m => (long?)m.sender_user_id)
                .FirstOrDefault(),
            c.ChatMessages.Count(m =>
                m.sender_user_id != participant.UserId && m.read_at == null)));
}
