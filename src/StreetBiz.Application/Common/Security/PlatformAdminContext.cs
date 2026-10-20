using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;

namespace StreetBiz.Application.Common.Security;

public interface IPlatformAdminContext
{
    Task<PlatformAdminActor> RequireAsync(CancellationToken cancellationToken);
}

public sealed class PlatformAdminContext(
    ICurrentUser currentUser,
    IUserAccountRepository users) : IPlatformAdminContext
{
    public async Task<PlatformAdminActor> RequireAsync(CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId
            ?? throw new AuthenticationException(AppMessages.SessionExpired);
        var user = await users.GetByIdAsync(userId, cancellationToken);

        if (currentUser.RoleCode != RoleCodes.PlatformAdmin
            || user is null
            || user.RoleCode != RoleCodes.PlatformAdmin
            || user.AccountStatus != AccountStatuses.Active)
        {
            throw new ForbiddenException("Only active platform administrators can perform this action.");
        }

        return new PlatformAdminActor(userId, user.FullName ?? $"Platform administrator #{userId}");
    }
}
