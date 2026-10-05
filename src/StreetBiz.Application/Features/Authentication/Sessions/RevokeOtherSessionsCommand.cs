using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.Application.Features.Authentication.Sessions;

/// <summary>AUTH-09: sign out every device except the one making the request.</summary>
public sealed record RevokeOtherSessionsCommand : IRequest<Unit>;

public sealed class RevokeOtherSessionsCommandHandler(
    ICurrentUser currentUser,
    ISessionRepository sessionRepository,
    ISecurityEvents securityEvents) : IRequestHandler<RevokeOtherSessionsCommand, Unit>
{
    public async Task<Unit> Handle(RevokeOtherSessionsCommand request, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId ?? throw new AuthenticationException(AppMessages.SessionExpired);
        var current = currentUser.SessionId ?? throw new AuthenticationException(AppMessages.SessionExpired);

        await sessionRepository.RevokeAllForUserExceptAsync(userId, current, cancellationToken);
        await securityEvents.RecordAsync(userId, SecurityActions.OtherSessionsRevoked, null, null, cancellationToken);
        return Unit.Value;
    }
}
