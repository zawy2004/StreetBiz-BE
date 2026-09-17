using StreetBiz.Application.Common.Models;
using StreetBiz.Application.DTOs.Authentication;

namespace StreetBiz.Application.Common.Security;

/// <summary>Creates a session + access/refresh tokens for an authenticated user.</summary>
public interface IAuthTokenIssuer
{
    Task<AuthResultDto> IssueAsync(
        AppUser user, string? deviceInfo, string? ipAddress, CancellationToken cancellationToken);
}
