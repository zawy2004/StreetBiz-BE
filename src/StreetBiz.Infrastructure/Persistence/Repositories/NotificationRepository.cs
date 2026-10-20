using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;

namespace StreetBiz.Infrastructure.Persistence.Repositories;

public sealed class NotificationRepository(StreetBizDbContext dbContext) : INotificationRepository
{
    public async Task<NotificationPage> ListAsync(
        long userId,
        long? beforeNotificationId,
        int take,
        CancellationToken cancellationToken)
    {
        var query = dbContext.Notifications
            .AsNoTracking()
            .Where(n => n.user_id == userId);

        if (beforeNotificationId is { } before)
        {
            // The cursor is the last notification of the previous page. It has to be
            // the caller's own: an unknown cursor ends the listing instead of leaking
            // where somebody else's notification sits in time.
            var anchor = await query
                .Where(n => n.notification_id == before)
                .Select(n => new { n.sent_at, n.notification_id })
                .FirstOrDefaultAsync(cancellationToken);
            if (anchor is null)
            {
                return new NotificationPage([], false);
            }

            query = query.Where(n => n.sent_at < anchor.sent_at
                || (n.sent_at == anchor.sent_at && n.notification_id < anchor.notification_id));
        }

        // Newest first by when it was sent, not by id: some writers (seed data, the
        // fee-reminder sweep) insert rows stamped earlier than rows already stored.
        // The id breaks ties so the keyset above never skips or repeats a row.
        var rows = await query
            .OrderByDescending(n => n.sent_at)
            .ThenByDescending(n => n.notification_id)
            .Take(take + 1)
            .Select(n => new NotificationRow(
                n.notification_id,
                n.notification_type,
                n.title,
                n.body,
                n.related_entity_type,
                n.related_entity_id,
                n.is_read,
                n.sent_at))
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > take;
        var items = rows
            .Take(take)
            // DATETIME2 comes back as Unspecified; every writer stores UTC, so say so
            // or the browser reads the time as local (seven hours off in Vietnam).
            .Select(row => row with { SentAt = DateTime.SpecifyKind(row.SentAt, DateTimeKind.Utc) })
            .ToList();

        return new NotificationPage(items, hasMore);
    }

    public Task<int> CountUnreadAsync(long userId, CancellationToken cancellationToken) =>
        dbContext.Notifications
            .AsNoTracking()
            .CountAsync(n => n.user_id == userId && !n.is_read, cancellationToken);

    public async Task<bool> MarkReadAsync(
        long userId,
        long notificationId,
        CancellationToken cancellationToken)
    {
        // Matching an already-read row still counts, so marking twice is not an error.
        var matched = await dbContext.Notifications
            .Where(n => n.notification_id == notificationId && n.user_id == userId)
            .ExecuteUpdateAsync(set => set.SetProperty(n => n.is_read, true), cancellationToken);
        return matched > 0;
    }

    public Task<int> MarkAllReadAsync(long userId, CancellationToken cancellationToken) =>
        dbContext.Notifications
            .Where(n => n.user_id == userId && !n.is_read)
            .ExecuteUpdateAsync(set => set.SetProperty(n => n.is_read, true), cancellationToken);
}
