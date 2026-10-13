using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StreetBiz.API.Hubs;
using StreetBiz.Application.Features.Chatbot;

namespace StreetBiz.API.Tests;

public sealed class ChatbotRealtimeTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, false, false)]
    public async Task Publisher_rechecks_each_recipient_session_scope_and_expiry(bool active, bool expired, bool otherScope)
    {
        var actor = new ChatbotActor(1, 1, "VENDOR", 10, 2);
        var recipients = new ChatbotSubscriptions();
        recipients.Set(new("connection", "conversation", otherScope ? actor with { WardId = 11 } : actor,
            DateTimeOffset.UtcNow.AddHours(expired ? -1 : 1)));
        var resolver = new Mock<IChatbotActorResolver>();
        resolver.Setup(a => a.IsActiveAsync(It.IsAny<ChatbotActor>(), It.IsAny<CancellationToken>())).ReturnsAsync(active);
        var store = new Mock<IChatbotStore>();
        store.Setup(s => s.RequireAsync(It.IsAny<ChatbotActor>(), "conversation", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatbotConversation("conversation", "", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null));
        using var services = new ServiceCollection().AddScoped(_ => resolver.Object).AddScoped(_ => store.Object).BuildServiceProvider();
        var client = new Mock<ISingleClientProxy>();
        client.Setup(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var hub = new Mock<IHubContext<ChatbotHub>>();
        hub.Setup(h => h.Clients.Client("connection")).Returns(client.Object);
        var publisher = new ChatbotEventPublisher(hub.Object, recipients, services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System, NullLogger<ChatbotEventPublisher>.Instance);
        await publisher.PublishAsync(actor, new("event", "conversation", "message", "request", 1, 1, 2, DateTimeOffset.UtcNow, "delta", new { delta = "private" }), default);
        client.Verify(c => c.SendCoreAsync("ChatbotEvent", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            active && !expired && !otherScope ? Times.Once() : Times.Never());
    }
}
