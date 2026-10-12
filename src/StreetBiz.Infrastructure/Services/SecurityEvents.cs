using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Services;

public sealed class SecurityEvents(StreetBizDbContext dbContext, IDateTimeProvider clock) : ISecurityEvents
{
    private const string EntityType = "UserAccount";

    public async Task RecordAsync(
        long userId,
        string action,
        string? details,
        (string Title, string Body)? notification,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        dbContext.AuditLogs.Add(new AuditLog
        {
            actor_user_id = userId,
            action = action,
            entity_type = EntityType,
            entity_id = userId,
            details = details is { Length: > 1000 } ? details[..1000] : details,
            created_at = now,
        });

        if (notification is { } n)
        {
            dbContext.Notifications.Add(new Notification
            {
                user_id = userId,
                notification_type = "SECURITY_ALERT",
                title = n.Title,
                body = n.Body,
                related_entity_type = EntityType,
                related_entity_id = userId,
                is_read = false,
                sent_at = now,
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SecurityEvent>> ListAsync(
        long userId, int take, long? beforeId, CancellationToken cancellationToken)
    {
        var query = dbContext.AuditLogs.AsNoTracking()
            .Where(a => a.actor_user_id == userId
                        && a.entity_type == EntityType
                        && SecurityActions.All.Contains(a.action));
        if (beforeId is { } before)
        {
            query = query.Where(a => a.audit_id < before);
        }

        var rows = await query
            .OrderByDescending(a => a.audit_id)
            .Take(Math.Clamp(take, 1, 50))
            .Select(a => new { a.audit_id, a.action, a.details, a.created_at })
            .ToListAsync(cancellationToken);
        return rows.Select(r => new SecurityEvent(r.audit_id, r.action, r.details, r.created_at)).ToList();
    }
}
