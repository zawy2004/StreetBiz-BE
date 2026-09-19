using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;

namespace StreetBiz.Infrastructure.Identity;

public sealed class JwtTokenService(
    IOptions<JwtSettings> options,
    IDateTimeProvider clock) : IJwtTokenService
{
    private readonly JwtSettings _settings = options.Value;

    public AccessTokenResult CreateAccessToken(AppUser user, long sessionId)
    {
        var now = clock.UtcNow;
        var expires = now.AddMinutes(_settings.AccessTokenMinutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new("sid", sessionId.ToString()),
            new("phone", user.PhoneNumber),
            new(ClaimTypes.Role, user.RoleCode),
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_settings.SigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _settings.Issuer,
            audience: _settings.Audience,
            claims: claims,
            notBefore: now,
            expires: expires,
            signingCredentials: credentials);

        var encoded = new JwtSecurityTokenHandler().WriteToken(token);
        return new AccessTokenResult(encoded, expires);
    }

    public RefreshTokenResult CreateRefreshToken()
    {
        var raw = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var hash = HashRefreshToken(raw);
        var expires = clock.UtcNow.AddDays(_settings.RefreshTokenDays);
        return new RefreshTokenResult(raw, hash, expires);
    }

    public byte[] HashRefreshToken(string rawToken) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));
}
