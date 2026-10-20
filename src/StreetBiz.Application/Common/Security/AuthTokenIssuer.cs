using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.DTOs.Authentication;

namespace StreetBiz.Application.Common.Security;

public sealed class AuthTokenIssuer(
    IJwtTokenService jwtTokenService,
    ISessionRepository sessionRepository) : IAuthTokenIssuer
{
    public Task<AuthResultDto> IssueAsync(
        AppUser user, string? deviceInfo, string? ipAddress, CancellationToken cancellationToken)
        => CreateAsync(user, deviceInfo, ipAddress, null, cancellationToken);

    public Task<AuthResultDto> RotateAsync(
        AppUser user, string? deviceInfo, string? ipAddress, DateTime sessionExpiresAtUtc,
        CancellationToken cancellationToken)
        => CreateAsync(user, deviceInfo, ipAddress, sessionExpiresAtUtc, cancellationToken);

    private async Task<AuthResultDto> CreateAsync(
        AppUser user, string? deviceInfo, string? ipAddress, DateTime? cap, CancellationToken cancellationToken)
    {
        var refresh = jwtTokenService.CreateRefreshToken();
        var expires = cap is { } c && c < refresh.ExpiresAtUtc ? c : refresh.ExpiresAtUtc;

        var sessionId = await sessionRepository.CreateAsync(
            user.Id, refresh.Hash, deviceInfo, ipAddress, expires, cancellationToken);

        var access = jwtTokenService.CreateAccessToken(user, sessionId);

        var userDto = new UserDto(
            user.Id, user.PhoneNumber, user.FullName, user.RoleCode, user.WardUnitId, user.AccountStatus);

        return new AuthResultDto(access.Token, access.ExpiresAtUtc, refresh.RawToken, userDto);
    }
}
