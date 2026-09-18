using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;

namespace StreetBiz.Application.Common.Security;

public interface ICustomerContext
{
    Task<long> RequireCustomerUserIdAsync(CancellationToken cancellationToken);
}

public sealed class CustomerContext(
    ICurrentUser currentUser,
    IUserAccountRepository users) : ICustomerContext
{
    public async Task<long> RequireCustomerUserIdAsync(CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId
            ?? throw new AuthenticationException("No active session.");
        var user = await users.GetByIdAsync(userId, cancellationToken);

        if (currentUser.RoleCode != RoleCodes.Customer
            || user is null
            || user.RoleCode != RoleCodes.Customer
            || user.AccountStatus != AccountStatuses.Active)
        {
            throw new ForbiddenException("Only active customer accounts can perform this action.");
        }

        return userId;
    }
}
