using Microsoft.AspNetCore.SignalR;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.DTOs.Chat;

namespace StreetBiz.API.Hubs;

public interface IChatRealtimePublisher
{
    Task PublishAsync(ChatMessageDto message, CancellationToken cancellationToken = default);
}

/// <summary>
/// Tells both sides of a thread that it has a new message: whoever has the
/// thread open, and each account's inbox, so the badge and the alert reach a
/// person who is on another screen entirely. Clients refetch over HTTP.
/// </summary>
public sealed class ChatRealtimePublisher(
    IHubContext<ChatHub> hub,
    IServiceScopeFactory scopes,
    ILogger<ChatRealtimePublisher> logger) : IChatRealtimePublisher
{
    public async Task PublishAsync(
        ChatMessageDto message,
        CancellationToken cancellationToken = default)
    {
        var payload = new ChatMessageReceivedMessage(
            message.ConversationId,
            message.MessageId,
            message.SenderUserId,
            message.Body,
            message.SentAt);
        try
        {
            await hub.Clients.Group(ChatHub.GroupName(message.ConversationId)).SendAsync(
                ChatHub.MessageReceivedEvent, payload, cancellationToken);

            await using var scope = scopes.CreateAsyncScope();
            var users = await scope.ServiceProvider
                .GetRequiredService<IChatRepository>()
                .GetThreadUsersAsync(message.ConversationId, cancellationToken);
            if (users is not null)
            {
                // The sender's own inbox too: their other devices list the
                // thread as most recent. Clients only alert for the other side.
                await hub.Clients.Groups(
                    ChatHub.InboxGroupName(users.CustomerUserId),
                    ChatHub.InboxGroupName(users.VendorUserId)).SendAsync(
                    ChatHub.InboxChangedEvent, payload, cancellationToken);
            }
        }
        catch (Exception exception)
        {
            // The message is already stored. A transport failure must not turn a
            // delivered message into an HTTP 500 - the recipient's next poll or
            // reconnect picks it up.
            logger.LogWarning(
                exception,
                "Could not publish realtime chat message {MessageId}.",
                message.MessageId);
        }
    }
}
