using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.DTOs.Notifications;

namespace StreetBiz.Application.Features.Notifications;

/// <summary>
/// The signed-in account's in-app notifications. Every role receives them (orders for
/// customers and sellers, fees for vendors, reviews for ward staff, moderation for
/// admins), so these handlers only need a signed-in user, not a particular role.
/// </summary>
public static class NotificationMessagesText
{
    public const string SignInRequired = "Vui lòng đăng nhập để xem thông báo.";
    public const string NotFound = "Không tìm thấy thông báo.";
}

internal static class NotificationUser
{
    public static long Require(ICurrentUser currentUser) =>
        currentUser.UserId ?? throw new AuthenticationException(NotificationMessagesText.SignInRequired);
}

public sealed record ListNotificationsQuery(long? BeforeNotificationId = null, int Take = 30)
    : IRequest<NotificationPageDto>;

public sealed class ListNotificationsQueryValidator : AbstractValidator<ListNotificationsQuery>
{
    public ListNotificationsQueryValidator()
    {
        RuleFor(x => x.BeforeNotificationId).GreaterThan(0).When(x => x.BeforeNotificationId is not null);
        RuleFor(x => x.Take).InclusiveBetween(1, 100);
    }
}

public sealed class ListNotificationsQueryHandler(
    ICurrentUser currentUser,
    INotificationRepository notifications) : IRequestHandler<ListNotificationsQuery, NotificationPageDto>
{
    public async Task<NotificationPageDto> Handle(
        ListNotificationsQuery request,
        CancellationToken cancellationToken)
    {
        var userId = NotificationUser.Require(currentUser);
        var page = await notifications.ListAsync(
            userId, request.BeforeNotificationId, request.Take, cancellationToken);
        return new NotificationPageDto(page.Items.Select(row => row.ToDto()).ToList(), page.HasMore);
    }
}

public sealed record GetNotificationUnreadCountQuery : IRequest<NotificationUnreadCountDto>;

public sealed class GetNotificationUnreadCountQueryHandler(
    ICurrentUser currentUser,
    INotificationRepository notifications)
    : IRequestHandler<GetNotificationUnreadCountQuery, NotificationUnreadCountDto>
{
    public async Task<NotificationUnreadCountDto> Handle(
        GetNotificationUnreadCountQuery request,
        CancellationToken cancellationToken)
    {
        var userId = NotificationUser.Require(currentUser);
        return new NotificationUnreadCountDto(
            await notifications.CountUnreadAsync(userId, cancellationToken));
    }
}

public sealed record MarkNotificationReadCommand(long NotificationId) : IRequest;

public sealed class MarkNotificationReadCommandValidator : AbstractValidator<MarkNotificationReadCommand>
{
    public MarkNotificationReadCommandValidator() =>
        RuleFor(x => x.NotificationId).GreaterThan(0);
}

public sealed class MarkNotificationReadCommandHandler(
    ICurrentUser currentUser,
    INotificationRepository notifications) : IRequestHandler<MarkNotificationReadCommand>
{
    public async Task Handle(MarkNotificationReadCommand request, CancellationToken cancellationToken)
    {
        var userId = NotificationUser.Require(currentUser);
        // Somebody else's notification is reported as missing, not forbidden, so ids
        // cannot be probed to learn what other accounts were told.
        if (!await notifications.MarkReadAsync(userId, request.NotificationId, cancellationToken))
        {
            throw new NotFoundException(NotificationMessagesText.NotFound);
        }
    }
}

public sealed record MarkAllNotificationsReadCommand : IRequest;

public sealed class MarkAllNotificationsReadCommandHandler(
    ICurrentUser currentUser,
    INotificationRepository notifications) : IRequestHandler<MarkAllNotificationsReadCommand>
{
    public async Task Handle(MarkAllNotificationsReadCommand request, CancellationToken cancellationToken)
    {
        var userId = NotificationUser.Require(currentUser);
        await notifications.MarkAllReadAsync(userId, cancellationToken);
    }
}

internal static class NotificationMappings
{
    public static NotificationDto ToDto(this NotificationRow row) =>
        new(
            row.NotificationId,
            row.Type,
            row.Title,
            row.Body,
            row.RelatedEntityType,
            row.RelatedEntityId,
            row.IsRead,
            row.SentAt);
}
