using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Chat;

namespace StreetBiz.Application.Features.Chat;

/// <summary>
/// Resolves which side of a thread the signed-in account is on. Chat is the one
/// area where a customer and a vendor read and write the same rows, so every
/// handler starts here rather than assuming a role.
/// </summary>
public interface IChatParticipantResolver
{
    Task<ChatParticipant> RequireParticipantAsync(CancellationToken cancellationToken);
}

public sealed class ChatParticipantResolver(ICurrentUser currentUser) : IChatParticipantResolver
{
    public Task<ChatParticipant> RequireParticipantAsync(CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            throw new AuthenticationException(ChatMessagesText.SignInRequired);
        }

        var side = currentUser.RoleCode switch
        {
            RoleCodes.Customer => ChatParticipantSide.Customer,
            RoleCodes.Vendor => ChatParticipantSide.Vendor,
            _ => throw new ForbiddenException(ChatMessagesText.RoleNotAllowed),
        };

        return Task.FromResult(new ChatParticipant(userId, side));
    }
}

public static class ChatMessagesText
{
    public const string SignInRequired = "Hãy đăng nhập để nhắn tin.";
    public const string RoleNotAllowed = "Chỉ người mua và người bán mới dùng được tin nhắn.";
    public const string ConversationNotFound = "Không tìm thấy cuộc trò chuyện.";
    public const string StorefrontNotFound = "Không tìm thấy gian hàng hoặc gian hàng không nhận tin nhắn.";
    public const string CustomerOnlyStart = "Chỉ người mua mới bắt đầu được cuộc trò chuyện với gian hàng.";
}

public sealed record ListChatConversationsQuery : IRequest<IReadOnlyList<ChatConversationDto>>;

public sealed class ListChatConversationsQueryHandler(
    IChatParticipantResolver participants,
    IChatRepository chat) : IRequestHandler<ListChatConversationsQuery, IReadOnlyList<ChatConversationDto>>
{
    public async Task<IReadOnlyList<ChatConversationDto>> Handle(
        ListChatConversationsQuery request,
        CancellationToken cancellationToken)
    {
        var participant = await participants.RequireParticipantAsync(cancellationToken);
        var rows = await chat.ListConversationsAsync(participant, cancellationToken);
        return rows.Select(row => row.ToDto(participant)).ToList();
    }
}

public sealed record GetChatThreadQuery(long ConversationId, int Take = 50)
    : IRequest<ChatThreadDto>;

public sealed class GetChatThreadQueryValidator : AbstractValidator<GetChatThreadQuery>
{
    public GetChatThreadQueryValidator()
    {
        RuleFor(x => x.ConversationId).GreaterThan(0);
        RuleFor(x => x.Take).InclusiveBetween(1, 200);
    }
}

public sealed class GetChatThreadQueryHandler(
    IChatParticipantResolver participants,
    IChatRepository chat) : IRequestHandler<GetChatThreadQuery, ChatThreadDto>
{
    public async Task<ChatThreadDto> Handle(
        GetChatThreadQuery request,
        CancellationToken cancellationToken)
    {
        var participant = await participants.RequireParticipantAsync(cancellationToken);
        var conversation = await chat.GetConversationAsync(
            participant, request.ConversationId, cancellationToken)
            ?? throw new NotFoundException(ChatMessagesText.ConversationNotFound);

        // Opening a thread is what marks it read; doing it here keeps the unread
        // badge honest without the client having to remember a second call. It
        // runs before the messages are read so the returned rows already carry
        // the read timestamps this call just set.
        await chat.MarkReadAsync(participant, request.ConversationId, cancellationToken);

        var page = await chat.ListMessagesAsync(
            participant, request.ConversationId, request.Take, null, cancellationToken);

        return new ChatThreadDto(
            conversation.ToDto(participant) with { UnreadCount = 0 },
            page.Messages.Select(message => message.ToDto(participant)).ToList(),
            page.HasMore);
    }
}

public sealed record GetOlderChatMessagesQuery(
    long ConversationId,
    long BeforeMessageId,
    int Take = 50) : IRequest<ChatMessagePageDto>;

public sealed class GetOlderChatMessagesQueryValidator
    : AbstractValidator<GetOlderChatMessagesQuery>
{
    public GetOlderChatMessagesQueryValidator()
    {
        RuleFor(x => x.ConversationId).GreaterThan(0);
        RuleFor(x => x.BeforeMessageId).GreaterThan(0);
        RuleFor(x => x.Take).InclusiveBetween(1, 200);
    }
}

public sealed class GetOlderChatMessagesQueryHandler(
    IChatParticipantResolver participants,
    IChatRepository chat) : IRequestHandler<GetOlderChatMessagesQuery, ChatMessagePageDto>
{
    public async Task<ChatMessagePageDto> Handle(
        GetOlderChatMessagesQuery request,
        CancellationToken cancellationToken)
    {
        var participant = await participants.RequireParticipantAsync(cancellationToken);
        _ = await chat.GetConversationAsync(
            participant, request.ConversationId, cancellationToken)
            ?? throw new NotFoundException(ChatMessagesText.ConversationNotFound);

        var page = await chat.ListMessagesAsync(
            participant,
            request.ConversationId,
            request.Take,
            request.BeforeMessageId,
            cancellationToken);

        return new ChatMessagePageDto(
            page.Messages.Select(message => message.ToDto(participant)).ToList(),
            page.HasMore);
    }
}

public sealed record StartChatConversationCommand(long StorefrontId)
    : IRequest<ChatConversationDto>;

public sealed class StartChatConversationCommandValidator
    : AbstractValidator<StartChatConversationCommand>
{
    public StartChatConversationCommandValidator() =>
        RuleFor(x => x.StorefrontId).GreaterThan(0);
}

public sealed class StartChatConversationCommandHandler(
    IChatParticipantResolver participants,
    IChatRepository chat) : IRequestHandler<StartChatConversationCommand, ChatConversationDto>
{
    public async Task<ChatConversationDto> Handle(
        StartChatConversationCommand request,
        CancellationToken cancellationToken)
    {
        var participant = await participants.RequireParticipantAsync(cancellationToken);
        if (participant.Side != ChatParticipantSide.Customer)
        {
            throw new ForbiddenException(ChatMessagesText.CustomerOnlyStart);
        }

        var conversation = await chat.StartConversationAsync(
            participant.UserId, request.StorefrontId, cancellationToken)
            ?? throw new NotFoundException(ChatMessagesText.StorefrontNotFound);

        return conversation.ToDto(participant);
    }
}

public sealed record SendChatMessageCommand(long ConversationId, string Body)
    : IRequest<ChatMessageDto>;

public sealed class SendChatMessageCommandValidator : AbstractValidator<SendChatMessageCommand>
{
    public SendChatMessageCommandValidator()
    {
        RuleFor(x => x.ConversationId).GreaterThan(0);
        RuleFor(x => x.Body)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .MaximumLength(2000);
    }
}

public sealed class SendChatMessageCommandHandler(
    IChatParticipantResolver participants,
    IChatRepository chat) : IRequestHandler<SendChatMessageCommand, ChatMessageDto>
{
    public async Task<ChatMessageDto> Handle(
        SendChatMessageCommand request,
        CancellationToken cancellationToken)
    {
        var participant = await participants.RequireParticipantAsync(cancellationToken);
        var message = await chat.SendMessageAsync(
            participant, request.ConversationId, request.Body.Trim(), cancellationToken)
            ?? throw new NotFoundException(ChatMessagesText.ConversationNotFound);

        return message.ToDto(participant);
    }
}

public sealed record GetChatUnreadCountQuery : IRequest<ChatUnreadCountDto>;

public sealed class GetChatUnreadCountQueryHandler(
    IChatParticipantResolver participants,
    IChatRepository chat) : IRequestHandler<GetChatUnreadCountQuery, ChatUnreadCountDto>
{
    public async Task<ChatUnreadCountDto> Handle(
        GetChatUnreadCountQuery request,
        CancellationToken cancellationToken)
    {
        var participant = await participants.RequireParticipantAsync(cancellationToken);
        return new ChatUnreadCountDto(await chat.CountUnreadAsync(participant, cancellationToken));
    }
}

internal static class ChatMappings
{
    public static ChatConversationDto ToDto(this ChatConversationRow row, ChatParticipant participant) =>
        new(
            row.ConversationId,
            row.StorefrontId,
            row.StorefrontName,
            row.StorefrontImageUrl,
            row.CustomerUserId,
            row.CustomerName,
            participant.Side == ChatParticipantSide.Customer ? row.StorefrontName : row.CustomerName,
            row.CreatedAt,
            row.LastMessageAt,
            row.LastMessageBody,
            row.LastMessageSenderUserId == participant.UserId,
            row.UnreadCount);

    public static ChatMessageDto ToDto(this ChatMessageRow row, ChatParticipant participant) =>
        new(
            row.MessageId,
            row.ConversationId,
            row.SenderUserId,
            row.SenderName,
            row.SenderUserId == participant.UserId,
            row.Body,
            row.SentAt,
            row.ReadAt);
}
