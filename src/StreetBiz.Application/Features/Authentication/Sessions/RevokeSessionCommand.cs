using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.Application.Features.Authentication.Sessions;

/// <summary>AUTH-09: revoke a specific session other than the current one.</summary>
public sealed record RevokeSessionCommand(long SessionId) : IRequest<Unit>;

public sealed class RevokeSessionCommandHandler(
    ICurrentUser currentUser,
    ISessionRepository sessionRepository) : IRequestHandler<RevokeSessionCommand, Unit>
{
    public async Task<Unit> Handle(RevokeSessionCommand request, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId ?? throw new AuthenticationException(AppMessages.SessionExpired);

        if (currentUser.SessionId is { } current && current == request.SessionId)
        {
            throw new DomainRuleException(AppMessages.CannotRevokeCurrent);
        }

        var session = await sessionRepository.GetByIdAsync(request.SessionId, cancellationToken)
            ?? throw new NotFoundException(AppMessages.SessionNotFound);

        if (session.UserId != userId)
        {
            throw new ForbiddenException(AppMessages.Forbidden);
        }

        await sessionRepository.RevokeAsync(request.SessionId, cancellationToken);
        return Unit.Value;
    }
}
