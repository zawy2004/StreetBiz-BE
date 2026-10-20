using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StreetBiz.API.Hubs;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.DTOs.Chat;
using StreetBiz.Application.Features.Chat;

namespace StreetBiz.API.Tests;

public sealed class ChatRealtimeTests
{
    private const long ConversationId = 12;
    private const long BuyerId = 7;
    private const long SellerUserId = 5;

    // ---- the inbox subscription ----

    private static (ChatHub Hub, Mock<IGroupManager> Groups) Hub(Func<ChatParticipant> caller)
    {
        var participants = new Mock<IChatParticipantResolver>();
        participants.Setup(x => x.RequireParticipantAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => caller());
        var context = new Mock<HubCallerContext>();
        context.SetupGet(x => x.ConnectionId).Returns("connection-1");
        context.SetupGet(x => x.ConnectionAborted).Returns(CancellationToken.None);
        var groups = new Mock<IGroupManager>();
        var hub = new ChatHub(participants.Object, Mock.Of<IChatRepository>())
        {
            Context = context.Object,
            Groups = groups.Object,
        };
        return (hub, groups);
    }

    [Fact]
    public async Task A_buyer_or_seller_follows_their_own_inbox()
    {
        var (hub, groups) = Hub(() => new ChatParticipant(BuyerId, ChatParticipantSide.Customer));

        await hub.SubscribeInbox();

        groups.Verify(x => x.AddToGroupAsync("connection-1", "inbox:7", It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task Anyone_without_chat_has_no_inbox_to_follow()
    {
        var (hub, groups) = Hub(() => throw new ForbiddenException("Chỉ người mua và người bán mới dùng được tin nhắn."));

        var action = () => hub.SubscribeInbox();

        await action.Should().ThrowAsync<HubException>();
        groups.VerifyNoOtherCalls();
    }

    // ---- publishing ----

    private static ChatMessageDto Message() => new(
        30, ConversationId, BuyerId, "Nguyễn Khách Hàng", true, "Còn bánh mì không quán?",
        new DateTime(2026, 10, 4, 7, 0, 0, DateTimeKind.Utc), null);

    private static (ChatRealtimePublisher Publisher, List<string> Sent, Mock<IChatRepository> Chat) Publisher()
    {
        var sent = new List<string>();
        IClientProxy Proxy(string target)
        {
            var proxy = new Mock<IClientProxy>();
            proxy.Setup(x => x.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
                .Callback((string method, object?[] _, CancellationToken _) => sent.Add($"{target} {method}"))
                .Returns(Task.CompletedTask);
            return proxy.Object;
        }
        var clients = new Mock<IHubClients>();
        clients.Setup(x => x.Group(It.IsAny<string>())).Returns((string group) => Proxy(group));
        clients.Setup(x => x.Groups(It.IsAny<IReadOnlyList<string>>()))
            .Returns((IReadOnlyList<string> groups) => Proxy(string.Join("+", groups)));
        var hub = new Mock<IHubContext<ChatHub>>();
        hub.SetupGet(x => x.Clients).Returns(clients.Object);

        var chat = new Mock<IChatRepository>();
        chat.Setup(x => x.GetThreadUsersAsync(ConversationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatThreadUsers(BuyerId, SellerUserId));
        var services = new ServiceCollection().AddScoped(_ => chat.Object).BuildServiceProvider();

        return (
            new ChatRealtimePublisher(
                hub.Object,
                services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<ChatRealtimePublisher>.Instance),
            sent,
            chat);
    }

    [Fact]
    public async Task A_message_reaches_the_open_thread_and_both_inboxes()
    {
        var (publisher, sent, _) = Publisher();

        await publisher.PublishAsync(Message());

        sent.Should().Equal(
            $"chat:12 {ChatHub.MessageReceivedEvent}",
            $"inbox:7+inbox:5 {ChatHub.InboxChangedEvent}");
    }

    [Fact]
    public async Task A_failed_lookup_never_fails_the_send_that_published()
    {
        var (publisher, sent, chat) = Publisher();
        chat.Setup(x => x.GetThreadUsersAsync(ConversationId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database down"));

        var action = () => publisher.PublishAsync(Message());

        await action.Should().NotThrowAsync();
        sent.Should().Equal($"chat:12 {ChatHub.MessageReceivedEvent}");
    }
}
