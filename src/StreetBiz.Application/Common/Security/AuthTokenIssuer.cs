using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.DTOs.Authentication;

namespace StreetBiz.Application.Common.Security;

public sealed class AuthTokenIssuer(
    IJwtTokenService jwtTokenService,
    ISessionRepository sessionRepository) : IAuthTokenIssuer
{
    public async Task<AuthResultDto> IssueAsync(
        AppUser user, string? deviceInfo, string? ipAddress, CancellationToken cancellationToken)
    {
        var refresh = jwtTokenService.CreateRefreshToken();

        var sessionId = await sessionRepository.CreateAsync(
            user.Id, refresh.Hash, deviceInfo, ipAddress, refresh.ExpiresAtUtc, cancellationToken);

        var access = jwtTokenService.CreateAccessToken(user, sessionId);

        var userDto = new UserDto(
            user.Id, user.PhoneNumber, user.FullName, user.RoleCode, user.WardUnitId, user.AccountStatus);

        return new AuthResultDto(access.Token, access.ExpiresAtUtc, refresh.RawToken, userDto);
    }
}
