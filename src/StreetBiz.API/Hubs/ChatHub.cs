using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Features.Chat;

namespace StreetBiz.API.Hubs;

/// <summary>
/// Delivers chat messages to whichever side is not the sender. Clients still
/// refetch the thread over HTTP; this only tells them when to.
/// </summary>
[Authorize]
public sealed class ChatHub(
    IChatParticipantResolver participants,
    IChatRepository chat) : Hub
{
    public const string MessageReceivedEvent = "ChatMessageReceived";

    /// <summary>One of the caller's threads has a new message, wherever they are in the app.</summary>
    public const string InboxChangedEvent = "ChatInboxChanged";

    public static string GroupName(long conversationId) => $"chat:{conversationId}";

    public static string InboxGroupName(long userId) => $"inbox:{userId}";

    /// <summary>
    /// Every thread of the caller, for the unread badge and new-message alerts
    /// on any screen. The account is read from the caller's own token and never
    /// passed in, so nobody can listen to somebody else's inbox.
    /// </summary>
    public async Task SubscribeInbox()
    {
        var userId = await CallerUserIdAsync()
            ?? throw new HubException("Only a buyer or a seller has an inbox.");
        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            InboxGroupName(userId),
            Context.ConnectionAborted);
    }

    public async Task UnsubscribeInbox()
    {
        if (await CallerUserIdAsync() is { } userId)
        {
            await Groups.RemoveFromGroupAsync(
                Context.ConnectionId,
                InboxGroupName(userId),
                Context.ConnectionAborted);
        }
    }

    public async Task SubscribeConversation(long conversationId)
    {
        if (conversationId <= 0 || !await CanAccessAsync(conversationId))
        {
            // Same reason as OrderHub: never confirm that a thread exists when it
            // belongs to somebody else.
            throw new HubException("Conversation was not found or is not available to this account.");
        }

        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            GroupName(conversationId),
            Context.ConnectionAborted);
    }

    public Task UnsubscribeConversation(long conversationId) => Groups.RemoveFromGroupAsync(
        Context.ConnectionId,
        GroupName(conversationId),
        Context.ConnectionAborted);

    private async Task<long?> CallerUserIdAsync()
    {
        try
        {
            return (await participants.RequireParticipantAsync(Context.ConnectionAborted)).UserId;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<bool> CanAccessAsync(long conversationId)
    {
        var cancellationToken = Context.ConnectionAborted;
        try
        {
            var participant = await participants.RequireParticipantAsync(cancellationToken);
            return await chat.GetConversationAsync(participant, conversationId, cancellationToken)
                is not null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }
}

public sealed record ChatMessageReceivedMessage(
    long ConversationId,
    long MessageId,
    long SenderUserId,
    string Body,
    DateTime SentAtUtc);
