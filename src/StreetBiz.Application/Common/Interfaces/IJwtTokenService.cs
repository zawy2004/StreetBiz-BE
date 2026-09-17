using StreetBiz.Application.Common.Models;

namespace StreetBiz.Application.Common.Interfaces;

public interface IJwtTokenService
{
    AccessTokenResult CreateAccessToken(AppUser user, long sessionId);
    RefreshTokenResult CreateRefreshToken();
    byte[] HashRefreshToken(string rawToken);
}
