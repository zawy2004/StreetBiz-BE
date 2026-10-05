using StreetBiz.Application.Common.Models;
using StreetBiz.Application.DTOs.Authentication;

namespace StreetBiz.Application.Common.Security;

/// <summary>Creates a session + access/refresh tokens for an authenticated user.</summary>
public interface IAuthTokenIssuer
{
    Task<AuthResultDto> IssueAsync(
        AppUser user, string? deviceInfo, string? ipAddress, CancellationToken cancellationToken);

    /// <summary>
    /// Issues the replacement session for a refresh. It keeps the original session's expiry, so a
    /// login has an absolute lifetime instead of being extended by every refresh.
    /// </summary>
    Task<AuthResultDto> RotateAsync(
        AppUser user, string? deviceInfo, string? ipAddress, DateTime sessionExpiresAtUtc,
        CancellationToken cancellationToken);
}
