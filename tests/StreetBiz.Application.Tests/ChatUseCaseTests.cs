using FluentAssertions;
using Moq;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.Chat;

namespace StreetBiz.Application.Tests;

public sealed class ChatUseCaseTests
{
    private const long CustomerUserId = 9;
    private const long VendorUserId = 5;

    private static ChatConversationRow Conversation(int unread = 0) => new(
        ConversationId: 1,
        StorefrontId: 3,
        StorefrontName: "Bánh mì & Xôi Cô Lan",
        StorefrontImageUrl: null,
        CustomerUserId: CustomerUserId,
        CustomerName: "Nguyễn Khách Hàng",
        CreatedAt: new DateTime(2026, 9, 27, 10, 0, 0, DateTimeKind.Utc),
        LastMessageAt: new DateTime(2026, 9, 27, 10, 5, 0, DateTimeKind.Utc),
        LastMessageBody: "Bánh mì còn không ạ?",
        LastMessageSenderUserId: CustomerUserId,
        UnreadCount: unread);

    private static ChatMessageRow Message(long senderUserId) => new(
        MessageId: 11,
        ConversationId: 1,
        SenderUserId: senderUserId,
        SenderName: senderUserId == CustomerUserId ? "Nguyễn Khách Hàng" : "Phạm Thị Lan",
        Body: "Bánh mì còn không ạ?",
        SentAt: new DateTime(2026, 9, 27, 10, 5, 0, DateTimeKind.Utc),
        ReadAt: null);

    private static IChatParticipantResolver Resolver(long userId, ChatParticipantSide side)
    {
        var resolver = new Mock<IChatParticipantResolver>();
        resolver.Setup(x => x.RequireParticipantAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatParticipant(userId, side));
        return resolver.Object;
    }

    private static IChatParticipantResolver CustomerResolver() =>
        Resolver(CustomerUserId, ChatParticipantSide.Customer);

    private static IChatParticipantResolver VendorResolver() =>
        Resolver(VendorUserId, ChatParticipantSide.Vendor);

    [Fact]
    public void Only_customers_and_vendors_may_use_chat()
    {
        var currentUser = new Mock<ICurrentUser>();
        currentUser.SetupGet(x => x.UserId).Returns(4);
        currentUser.SetupGet(x => x.RoleCode).Returns(RoleCodes.WardAuthority);

        var action = () => new ChatParticipantResolver(currentUser.Object)
            .RequireParticipantAsync(default);

        action.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task Each_side_sees_the_other_as_the_thread_title()
    {
        var repository = new Mock<IChatRepository>();
        repository.Setup(x => x.ListConversationsAsync(
                It.IsAny<ChatParticipant>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Conversation()]);

        var forCustomer = await new ListChatConversationsQueryHandler(
            CustomerResolver(), repository.Object).Handle(new(), default);
        var forVendor = await new ListChatConversationsQueryHandler(
            VendorResolver(), repository.Object).Handle(new(), default);

        forCustomer[0].CounterpartName.Should().Be("Bánh mì & Xôi Cô Lan");
        forVendor[0].CounterpartName.Should().Be("Nguyễn Khách Hàng");
    }

    [Fact]
    public async Task A_message_is_mine_only_for_the_account_that_sent_it()
    {
        var repository = new Mock<IChatRepository>();
        repository.Setup(x => x.GetConversationAsync(
                It.IsAny<ChatParticipant>(), 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Conversation());
        repository.Setup(x => x.ListMessagesAsync(
                It.IsAny<ChatParticipant>(), 1, It.IsAny<int>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatMessagePage([Message(CustomerUserId)], false));

        var customerView = await new GetChatThreadQueryHandler(
            CustomerResolver(), repository.Object).Handle(new(1), default);
        var vendorView = await new GetChatThreadQueryHandler(
            VendorResolver(), repository.Object).Handle(new(1), default);

        customerView.Messages[0].FromMe.Should().BeTrue();
        vendorView.Messages[0].FromMe.Should().BeFalse();
    }

    [Fact]
    public async Task Opening_a_thread_marks_it_read_and_clears_its_badge()
    {
        var repository = new Mock<IChatRepository>();
        repository.Setup(x => x.GetConversationAsync(
                It.IsAny<ChatParticipant>(), 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Conversation(unread: 3));
        repository.Setup(x => x.ListMessagesAsync(
                It.IsAny<ChatParticipant>(), 1, It.IsAny<int>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatMessagePage([], false));
        repository.Setup(x => x.MarkReadAsync(
                It.IsAny<ChatParticipant>(), 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);

        var thread = await new GetChatThreadQueryHandler(
            VendorResolver(), repository.Object).Handle(new(1), default);

        thread.Conversation.UnreadCount.Should().Be(0);
        repository.Verify(x => x.MarkReadAsync(
            It.IsAny<ChatParticipant>(), 1, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Opening_a_thread_reports_whether_older_messages_remain()
    {
        var repository = new Mock<IChatRepository>();
        repository.Setup(x => x.GetConversationAsync(
                It.IsAny<ChatParticipant>(), 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Conversation());
        repository.Setup(x => x.ListMessagesAsync(
                It.IsAny<ChatParticipant>(), 1, It.IsAny<int>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatMessagePage([Message(CustomerUserId)], true));

        var thread = await new GetChatThreadQueryHandler(CustomerResolver(), repository.Object)
            .Handle(new(1), default);

        thread.HasMore.Should().BeTrue();
    }

    [Fact]
    public async Task Older_messages_are_read_from_before_the_given_id()
    {
        var repository = new Mock<IChatRepository>();
        repository.Setup(x => x.GetConversationAsync(
                It.IsAny<ChatParticipant>(), 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Conversation());
        repository.Setup(x => x.ListMessagesAsync(
                It.IsAny<ChatParticipant>(), 1, 50, 11L, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatMessagePage([Message(VendorUserId)], false));

        var page = await new GetOlderChatMessagesQueryHandler(
            CustomerResolver(), repository.Object).Handle(new(1, 11), default);

        page.Messages.Should().HaveCount(1);
        page.HasMore.Should().BeFalse();
        repository.VerifyAll();
    }

    [Fact]
    public async Task Older_messages_of_a_thread_that_is_not_mine_read_as_not_found()
    {
        var repository = new Mock<IChatRepository>();
        repository.Setup(x => x.GetConversationAsync(
                It.IsAny<ChatParticipant>(), 99, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ChatConversationRow?)null);
        var handler = new GetOlderChatMessagesQueryHandler(CustomerResolver(), repository.Object);

        var action = () => handler.Handle(new(99, 5), default);

        await action.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task A_thread_owned_by_someone_else_reads_as_not_found()
    {
        var repository = new Mock<IChatRepository>();
        repository.Setup(x => x.GetConversationAsync(
                It.IsAny<ChatParticipant>(), 99, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ChatConversationRow?)null);
        var handler = new GetChatThreadQueryHandler(CustomerResolver(), repository.Object);

        var action = () => handler.Handle(new(99), default);

        await action.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task A_vendor_cannot_open_a_conversation_with_a_customer()
    {
        var repository = new Mock<IChatRepository>(MockBehavior.Strict);
        var handler = new StartChatConversationCommandHandler(
            VendorResolver(), repository.Object);

        var action = () => handler.Handle(new(3), default);

        await action.Should().ThrowAsync<ForbiddenException>();
        // Strict mock: the handler must refuse before touching the database.
        repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Sending_trims_the_body_before_it_is_stored()
    {
        var repository = new Mock<IChatRepository>();
        repository.Setup(x => x.SendMessageAsync(
                It.IsAny<ChatParticipant>(), 1, "Bánh mì còn không ạ?", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Message(CustomerUserId));

        await new SendChatMessageCommandHandler(CustomerResolver(), repository.Object)
            .Handle(new(1, "   Bánh mì còn không ạ?  "), default);

        repository.VerifyAll();
    }

    [Fact]
    public async Task Sending_to_a_thread_that_is_not_mine_reads_as_not_found()
    {
        var repository = new Mock<IChatRepository>();
        repository.Setup(x => x.SendMessageAsync(
                It.IsAny<ChatParticipant>(), 99, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ChatMessageRow?)null);
        var handler = new SendChatMessageCommandHandler(CustomerResolver(), repository.Object);

        var action = () => handler.Handle(new(99, "xin chào"), default);

        await action.Should().ThrowAsync<NotFoundException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_message_is_rejected(string body)
    {
        var result = new SendChatMessageCommandValidator().Validate(new SendChatMessageCommand(1, body));
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void A_message_longer_than_the_column_is_rejected()
    {
        var result = new SendChatMessageCommandValidator()
            .Validate(new SendChatMessageCommand(1, new string('a', 2001)));
        result.IsValid.Should().BeFalse();
    }
}
