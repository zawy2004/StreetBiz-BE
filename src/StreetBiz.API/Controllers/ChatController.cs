using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using StreetBiz.API.Hubs;
using StreetBiz.Application.DTOs.Chat;
using StreetBiz.Application.Features.Chat;

namespace StreetBiz.API.Controllers;

/// <summary>
/// CHAT-01/02: direct messages between a customer and a storefront's seller.
/// Both roles use the same routes; the handler decides which side is asking.
/// </summary>
[ApiController]
[Authorize]
[Route("api/chat")]
public sealed class ChatController(
    ISender sender,
    IChatRealtimePublisher realtime) : ControllerBase
{
    /// <summary>Threads the signed-in account takes part in, most recent first.</summary>
    [HttpGet("conversations")]
    public async Task<ActionResult<IReadOnlyList<ChatConversationDto>>> Conversations(
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(new ListChatConversationsQuery(), cancellationToken));

    /// <summary>Opens (or reuses) the customer's thread with a storefront.</summary>
    [HttpPost("conversations")]
    public async Task<ActionResult<ChatConversationDto>> Start(
        StartChatConversationRequest request,
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(
            new StartChatConversationCommand(request.StorefrontId), cancellationToken));

    /// <summary>The newest page of a thread. Reading it also marks it read.</summary>
    [HttpGet("conversations/{conversationId:long}")]
    public async Task<ActionResult<ChatThreadDto>> Thread(
        long conversationId,
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default) =>
        Ok(await sender.Send(new GetChatThreadQuery(conversationId, take), cancellationToken));

    /// <summary>Older messages, for scrolling back through a long thread.</summary>
    [HttpGet("conversations/{conversationId:long}/messages")]
    public async Task<ActionResult<ChatMessagePageDto>> OlderMessages(
        long conversationId,
        [FromQuery] long before,
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default) =>
        Ok(await sender.Send(
            new GetOlderChatMessagesQuery(conversationId, before, take), cancellationToken));

    [EnableRateLimiting("ChatSend")]
    [HttpPost("conversations/{conversationId:long}/messages")]
    public async Task<ActionResult<ChatMessageDto>> Send(
        long conversationId,
        SendChatMessageRequest request,
        CancellationToken cancellationToken)
    {
        var message = await sender.Send(
            new SendChatMessageCommand(conversationId, request.Body), cancellationToken);
        await realtime.PublishAsync(message, cancellationToken);
        return Ok(message);
    }

    /// <summary>Unread total across every thread, for the navigation badge.</summary>
    [HttpGet("unread-count")]
    public async Task<ActionResult<ChatUnreadCountDto>> UnreadCount(
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(new GetChatUnreadCountQuery(), cancellationToken));
}

public sealed record StartChatConversationRequest(long StorefrontId);
public sealed record SendChatMessageRequest(string Body);
