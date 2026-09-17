using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Persistence.Repositories;

public sealed class SessionRepository(
    StreetBizDbContext dbContext,
    IDateTimeProvider clock) : ISessionRepository
{
    public async Task<long> CreateAsync(
        long userId, byte[] refreshTokenHash, string? deviceInfo, string? ipAddress,
        DateTime expiresAtUtc, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var entity = new UserSession
        {
            user_id = userId,
            refresh_token_hash = refreshTokenHash,
            device_info = deviceInfo,
            ip_address = ipAddress,
            expires_at = expiresAtUtc,
            created_at = now,
            last_active_at = now,
        };

        dbContext.UserSessions.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return entity.session_id;
    }

    public async Task<AppSession?> GetActiveByRefreshHashAsync(byte[] refreshTokenHash, CancellationToken cancellationToken)
    {
        var entity = await dbContext.UserSessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.refresh_token_hash == refreshTokenHash, cancellationToken);
        return entity is null ? null : Map(entity);
    }

    public async Task<AppSession?> GetByIdAsync(long sessionId, CancellationToken cancellationToken)
    {
        var entity = await dbContext.UserSessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.session_id == sessionId, cancellationToken);
        return entity is null ? null : Map(entity);
    }

    public async Task<IReadOnlyList<AppSession>> ListActiveByUserAsync(long userId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var entities = await dbContext.UserSessions.AsNoTracking()
            .Where(s => s.user_id == userId && s.revoked_at == null && s.expires_at > now)
            .OrderByDescending(s => s.last_active_at)
            .ToListAsync(cancellationToken);
        return entities.Select(Map).ToList();
    }

    public async Task RevokeAsync(long sessionId, CancellationToken cancellationToken)
    {
        var entity = await dbContext.UserSessions
            .FirstOrDefaultAsync(s => s.session_id == sessionId, cancellationToken);
        if (entity is null || entity.revoked_at is not null)
        {
            return;
        }

        entity.revoked_at = clock.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task RevokeAllForUserAsync(long userId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var sessions = await dbContext.UserSessions
            .Where(s => s.user_id == userId && s.revoked_at == null)
            .ToListAsync(cancellationToken);
        foreach (var s in sessions)
        {
            s.revoked_at = now;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task RevokeAllForUserExceptAsync(long userId, long keepSessionId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var sessions = await dbContext.UserSessions
            .Where(s => s.user_id == userId && s.revoked_at == null && s.session_id != keepSessionId)
            .ToListAsync(cancellationToken);
        foreach (var s in sessions)
        {
            s.revoked_at = now;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task TouchAsync(long sessionId, CancellationToken cancellationToken)
    {
        var entity = await dbContext.UserSessions
            .FirstOrDefaultAsync(s => s.session_id == sessionId, cancellationToken);
        if (entity is null)
        {
            return;
        }

        entity.last_active_at = clock.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static AppSession Map(UserSession s) => new(
        s.session_id, s.user_id, s.device_info, s.ip_address, s.created_at,
        s.last_active_at, s.expires_at, s.revoked_at);
}
