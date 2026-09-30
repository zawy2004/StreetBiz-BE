using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StreetBiz.Application.DTOs.Notifications;
using StreetBiz.Application.Features.Notifications;

namespace StreetBiz.API.Controllers;

/// <summary>
/// The signed-in account's in-app notifications, for any role. The rows are written by
/// the workflows themselves (orders, fees, reviews, food safety, moderation).
/// </summary>
[ApiController]
[Authorize]
[Route("api/notifications")]
public sealed class NotificationsController(ISender sender) : ControllerBase
{
    /// <summary>Newest first. Pass the last id you have as <c>before</c> for the next page.</summary>
    [HttpGet]
    public async Task<ActionResult<NotificationPageDto>> List(
        [FromQuery] long? before,
        [FromQuery] int take = 30,
        CancellationToken cancellationToken = default) =>
        Ok(await sender.Send(new ListNotificationsQuery(before, take), cancellationToken));

    /// <summary>Unread total, for the navigation badge.</summary>
    [HttpGet("unread-count")]
    public async Task<ActionResult<NotificationUnreadCountDto>> UnreadCount(
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(new GetNotificationUnreadCountQuery(), cancellationToken));

    [HttpPost("{notificationId:long}/read")]
    public async Task<IActionResult> MarkRead(long notificationId, CancellationToken cancellationToken)
    {
        await sender.Send(new MarkNotificationReadCommand(notificationId), cancellationToken);
        return NoContent();
    }

    [HttpPost("read-all")]
    public async Task<IActionResult> MarkAllRead(CancellationToken cancellationToken)
    {
        await sender.Send(new MarkAllNotificationsReadCommand(), cancellationToken);
        return NoContent();
    }
}
