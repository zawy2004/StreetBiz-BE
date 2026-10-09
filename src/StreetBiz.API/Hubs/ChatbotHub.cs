using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using StreetBiz.Application.Features.Chatbot;

namespace StreetBiz.API.Hubs;

public sealed record ChatbotSubscription(string ConnectionId, string ConversationId, ChatbotActor Actor, DateTimeOffset ExpiresAt);
public sealed class ChatbotSubscriptions
{
    private readonly ConcurrentDictionary<string, ChatbotSubscription> entries = new();
    public void Set(ChatbotSubscription entry)
    {
        Remove(entry.ConnectionId); // One visible conversation per connection.
        entries[entry.ConnectionId] = entry;
    }
    public void Remove(string connectionId) => entries.TryRemove(connectionId, out _);
    public ChatbotSubscription[] ForConversation(string conversation) => entries.Values.Where(e => e.ConversationId == conversation).ToArray();
}

[Authorize]
public sealed class ChatbotHub(IChatbotActorResolver actors, IChatbotStore store, ChatbotSubscriptions subscriptions,
    ChatbotSettings settings) : Hub
{
    public async Task SubscribeConversation(string conversationId)
    {
        try
        {
            if (!settings.Enabled) throw new HubException("Trợ lý đang tạm nghỉ.");
            var actor = await actors.RequireAsync(Context.ConnectionAborted);
            await store.RequireAsync(actor, conversationId, Context.ConnectionAborted);
            if (!long.TryParse(Context.User?.FindFirstValue("exp"), out var exp)) throw new HubException("Phiên không hợp lệ.");
            subscriptions.Set(new(Context.ConnectionId, conversationId, actor, DateTimeOffset.FromUnixTimeSeconds(exp)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new HubException("Không tìm thấy hội thoại hoặc phiên không còn quyền truy cập.");
        }
    }
    public Task UnsubscribeConversation(string conversationId) { subscriptions.Remove(Context.ConnectionId); return Task.CompletedTask; }
    public override Task OnDisconnectedAsync(Exception? exception) { subscriptions.Remove(Context.ConnectionId); return base.OnDisconnectedAsync(exception); }
}

/// <summary>Revalidates each recipient, not only the generating request. Never broadcasts into a stale authorized group.</summary>
public sealed class ChatbotEventPublisher(IHubContext<ChatbotHub> hub, ChatbotSubscriptions subscriptions,
    IServiceScopeFactory scopeFactory, TimeProvider clock, ILogger<ChatbotEventPublisher> logger) : IChatbotEvents
{
    public async Task PublishAsync(ChatbotActor actor, ChatbotEvent message, CancellationToken ct)
    {
        foreach (var recipient in subscriptions.ForConversation(message.ConversationId))
        {
            if (recipient.Actor.UserId != actor.UserId || recipient.Actor.Scope != actor.Scope || recipient.ExpiresAt <= clock.GetUtcNow())
            {
                subscriptions.Remove(recipient.ConnectionId); continue;
            }
            await using var scope = scopeFactory.CreateAsyncScope();
            var access = scope.ServiceProvider.GetRequiredService<IChatbotActorResolver>();
            try
            {
                if (!await access.IsActiveAsync(recipient.Actor, ct)) { subscriptions.Remove(recipient.ConnectionId); continue; }
                // Deletion and scope changes also invalidate existing subscriptions.
                await scope.ServiceProvider.GetRequiredService<IChatbotStore>().RequireAsync(recipient.Actor, message.ConversationId, ct);
                await hub.Clients.Client(recipient.ConnectionId).SendAsync("ChatbotEvent", message, ct);
            }
            catch (ChatbotException) { subscriptions.Remove(recipient.ConnectionId); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Chatbot delivery unavailable ({ErrorType}); REST snapshot remains authoritative.", ex.GetType().Name);
            }
        }
    }
}
