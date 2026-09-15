using StreetBiz.Application.Common.Models;

namespace StreetBiz.Application.Common.Interfaces;

public interface ISessionRepository
{
    Task<long> CreateAsync(
        long userId, byte[] refreshTokenHash, string? deviceInfo, string? ipAddress,
        DateTime expiresAtUtc, CancellationToken cancellationToken);

    Task<AppSession?> GetActiveByRefreshHashAsync(byte[] refreshTokenHash, CancellationToken cancellationToken);
    Task<AppSession?> GetByIdAsync(long sessionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<AppSession>> ListActiveByUserAsync(long userId, CancellationToken cancellationToken);

    Task RevokeAsync(long sessionId, CancellationToken cancellationToken);
    Task RevokeAllForUserAsync(long userId, CancellationToken cancellationToken);
    Task RevokeAllForUserExceptAsync(long userId, long keepSessionId, CancellationToken cancellationToken);
    Task TouchAsync(long sessionId, CancellationToken cancellationToken);
}
