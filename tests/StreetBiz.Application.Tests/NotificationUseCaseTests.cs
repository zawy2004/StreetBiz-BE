using FluentAssertions;
using Moq;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Features.Notifications;

namespace StreetBiz.Application.Tests;

public sealed class NotificationUseCaseTests
{
    private const long UserId = 9;

    private static ICurrentUser SignedIn(long? userId = UserId)
    {
        var currentUser = new Mock<ICurrentUser>();
        currentUser.SetupGet(x => x.UserId).Returns(userId);
        return currentUser.Object;
    }

    private static NotificationRow Row(long id, bool isRead = false) => new(
        NotificationId: id,
        Type: "ORDER_STATUS",
        Title: "Đơn hàng đã sẵn sàng",
        Body: "Đơn SB-1 đã sẵn sàng để nhận.",
        RelatedEntityType: "ORDER",
        RelatedEntityId: 7,
        IsRead: isRead,
        SentAt: new DateTime(2026, 9, 30, 10, 0, 0, DateTimeKind.Utc));

    [Fact]
    public async Task Lists_only_the_signed_in_users_page()
    {
        var repository = new Mock<INotificationRepository>();
        repository.Setup(x => x.ListAsync(UserId, 50, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationPage([Row(12), Row(11, isRead: true)], HasMore: true));

        var page = await new ListNotificationsQueryHandler(SignedIn(), repository.Object)
            .Handle(new ListNotificationsQuery(50, 20), default);

        page.HasMore.Should().BeTrue();
        page.Items.Select(x => x.NotificationId).Should().Equal(12, 11);
        page.Items[1].IsRead.Should().BeTrue();
        page.Items[0].RelatedEntityType.Should().Be("ORDER");
    }

    [Fact]
    public async Task Marking_a_missing_or_foreign_notification_is_not_found()
    {
        var repository = new Mock<INotificationRepository>();
        repository.Setup(x => x.MarkReadAsync(UserId, 99, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var action = () => new MarkNotificationReadCommandHandler(SignedIn(), repository.Object)
            .Handle(new MarkNotificationReadCommand(99), default);

        await action.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Unread_count_and_mark_all_use_the_signed_in_user()
    {
        var repository = new Mock<INotificationRepository>();
        repository.Setup(x => x.CountUnreadAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);

        var count = await new GetNotificationUnreadCountQueryHandler(SignedIn(), repository.Object)
            .Handle(new GetNotificationUnreadCountQuery(), default);
        await new MarkAllNotificationsReadCommandHandler(SignedIn(), repository.Object)
            .Handle(new MarkAllNotificationsReadCommand(), default);

        count.UnreadCount.Should().Be(3);
        repository.Verify(x => x.MarkAllReadAsync(UserId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Anonymous_callers_are_rejected()
    {
        var action = () => new GetNotificationUnreadCountQueryHandler(
                SignedIn(userId: null), Mock.Of<INotificationRepository>())
            .Handle(new GetNotificationUnreadCountQuery(), default);

        await action.Should().ThrowAsync<AuthenticationException>();
    }

    [Theory]
    [InlineData(null, 30, true)]
    [InlineData(10L, 100, true)]
    [InlineData(0L, 30, false)]
    [InlineData(null, 0, false)]
    [InlineData(null, 101, false)]
    public void Validates_paging(long? before, int take, bool valid)
    {
        var result = new ListNotificationsQueryValidator()
            .Validate(new ListNotificationsQuery(before, take));

        result.IsValid.Should().Be(valid);
    }
}
