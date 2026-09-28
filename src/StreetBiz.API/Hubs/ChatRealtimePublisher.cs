using Microsoft.AspNetCore.SignalR;
using StreetBiz.Application.DTOs.Chat;

namespace StreetBiz.API.Hubs;

public interface IChatRealtimePublisher
{
    Task PublishAsync(ChatMessageDto message, CancellationToken cancellationToken = default);
}

public sealed class ChatRealtimePublisher(
    IHubContext<ChatHub> hub,
    ILogger<ChatRealtimePublisher> logger) : IChatRealtimePublisher
{
    public async Task PublishAsync(
        ChatMessageDto message,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await hub.Clients.Group(ChatHub.GroupName(message.ConversationId)).SendAsync(
                ChatHub.MessageReceivedEvent,
                new ChatMessageReceivedMessage(
                    message.ConversationId,
                    message.MessageId,
                    message.SenderUserId,
                    message.Body,
                    message.SentAt),
                cancellationToken);
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
