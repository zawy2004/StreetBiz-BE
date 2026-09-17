using System.Security.Claims;
using StreetBiz.Application.Common.Interfaces;

namespace StreetBiz.API.Extensions;

/// <summary>Resolves the authenticated caller from the JWT claims on the current request.</summary>
public sealed class CurrentUser(IHttpContextAccessor httpContextAccessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => httpContextAccessor.HttpContext?.User;

    public long? UserId => ParseLong(Principal?.FindFirstValue("sub"));

    public long? SessionId => ParseLong(Principal?.FindFirstValue("sid"));

    public string? RoleCode => Principal?.FindFirstValue(ClaimTypes.Role);

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated ?? false;

    private static long? ParseLong(string? value) =>
        long.TryParse(value, out var result) ? result : null;
}
