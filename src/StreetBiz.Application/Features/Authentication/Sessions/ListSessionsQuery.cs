using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.DTOs.Authentication;

namespace StreetBiz.Application.Features.Authentication.Sessions;

/// <summary>AUTH-08: list the caller's active sessions, marking the current one.</summary>
public sealed record ListSessionsQuery : IRequest<IReadOnlyList<SessionDto>>;

public sealed class ListSessionsQueryHandler(
    ICurrentUser currentUser,
    ISessionRepository sessionRepository)
    : IRequestHandler<ListSessionsQuery, IReadOnlyList<SessionDto>>
{
    public async Task<IReadOnlyList<SessionDto>> Handle(ListSessionsQuery request, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId ?? throw new AuthenticationException("No active session.");
        var current = currentUser.SessionId;

        var sessions = await sessionRepository.ListActiveByUserAsync(userId, cancellationToken);

        return sessions
            .Select(s => new SessionDto(
                s.Id, s.DeviceInfo, s.IpAddress, s.CreatedAt, s.LastActiveAt, s.ExpiresAt,
                IsCurrent: current is not null && s.Id == current))
            .ToList();
    }
}
