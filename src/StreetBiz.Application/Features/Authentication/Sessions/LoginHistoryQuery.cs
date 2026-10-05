using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.Application.Features.Authentication.Sessions;

/// <summary>The caller's own sign-in and security history, newest first.</summary>
public sealed record LoginHistoryQuery(int Take = 20, long? Before = null) : IRequest<IReadOnlyList<SecurityEvent>>;

public sealed class LoginHistoryQueryHandler(
    ICurrentUser currentUser,
    ISecurityEvents securityEvents) : IRequestHandler<LoginHistoryQuery, IReadOnlyList<SecurityEvent>>
{
    public Task<IReadOnlyList<SecurityEvent>> Handle(LoginHistoryQuery request, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId ?? throw new AuthenticationException(AppMessages.SessionExpired);
        return securityEvents.ListAsync(userId, request.Take, request.Before, cancellationToken);
    }
}
