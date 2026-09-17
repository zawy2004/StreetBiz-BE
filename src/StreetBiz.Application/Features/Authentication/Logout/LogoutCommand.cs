using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;

namespace StreetBiz.Application.Features.Authentication.Logout;

/// <summary>AUTH-04: revoke the current session.</summary>
public sealed record LogoutCommand : IRequest<Unit>;

public sealed class LogoutCommandHandler(
    ICurrentUser currentUser,
    ISessionRepository sessionRepository) : IRequestHandler<LogoutCommand, Unit>
{
    public async Task<Unit> Handle(LogoutCommand request, CancellationToken cancellationToken)
    {
        var sessionId = currentUser.SessionId
            ?? throw new AuthenticationException("No active session.");

        await sessionRepository.RevokeAsync(sessionId, cancellationToken);
        return Unit.Value;
    }
}
