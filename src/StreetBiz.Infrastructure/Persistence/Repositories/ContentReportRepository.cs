using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.Commerce;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Persistence.Repositories;

public sealed class ContentReportRepository(StreetBizDbContext db, TimeProvider clock) : IContentReportRepository
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<ContentReportResult> CreateAsync(ContentReportDraft draft, CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken);

            var ownerUserId = await ReportableOwnerAsync(draft, cancellationToken);
            if (ownerUserId is null)
            {
                return new ContentReportResult(ContentReportOutcome.ContentNotFound);
            }

            if (ownerUserId == draft.ReporterUserId)
            {
                return new ContentReportResult(ContentReportOutcome.OwnContent);
            }

            if (await db.ReportedContents.AnyAsync(
                    report => report.reporter_user_id == draft.ReporterUserId
                        && report.content_type == draft.ContentType
                        && report.content_id == draft.ContentId
                        && report.status == ReportedContentStatuses.Pending,
                    cancellationToken))
            {
                return new ContentReportResult(ContentReportOutcome.Duplicate);
            }

            var now = Now;
            var since = now - ContentReportLimits.Window;
            var recent = await db.ReportedContents
                .Where(report => report.reporter_user_id == draft.ReporterUserId && report.created_at > since)
                .OrderBy(report => report.created_at)
                .Select(report => report.created_at)
                .Take(ContentReportLimits.MaxPerHour)
                .ToListAsync(cancellationToken);
            if (recent.Count >= ContentReportLimits.MaxPerHour)
            {
                // The oldest report in the window leaves it first; that is when one more is allowed.
                var retryAfter = (int)Math.Ceiling((recent[0] + ContentReportLimits.Window - now).TotalSeconds);
                return new ContentReportResult(ContentReportOutcome.RateLimited, RetryAfterSeconds: retryAfter);
            }

            var report = new ReportedContent
            {
                content_type = draft.ContentType,
                content_id = draft.ContentId,
                reporter_user_id = draft.ReporterUserId,
                reason = draft.Reason,
                status = ReportedContentStatuses.Pending,
                created_at = now,
            };
            db.ReportedContents.Add(report);
            await db.SaveChangesAsync(cancellationToken);
            db.AuditLogs.Add(new AuditLog
            {
                actor_user_id = draft.ReporterUserId,
                action = "CONTENT_REPORT_CREATE",
                entity_type = "ReportedContent",
                entity_id = report.content_report_id,
                details = JsonSerializer.Serialize(new { draft.ContentType, draft.ContentId }),
                created_at = now,
            });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new ContentReportResult(ContentReportOutcome.Created, report.content_report_id);
        });
    }

    /// <summary>The seller's user id when the content exists and is still visible, otherwise null.</summary>
    private async Task<long?> ReportableOwnerAsync(ContentReportDraft draft, CancellationToken ct)
    {
        switch (draft.ContentType)
        {
            case ReportedContentTypes.Storefront:
                var storefrontOwner = await db.Storefronts.AsNoTracking()
                    .Where(storefront => storefront.storefront_id == draft.ContentId)
                    .Select(storefront => (long?)storefront.registration.vendor.user_id)
                    .SingleOrDefaultAsync(ct);
                // A moderator hides a storefront by pausing it and marking a report HIDDEN; that report is the marker.
                return storefrontOwner is not null && !await IsHiddenAsync(draft, ct) ? storefrontOwner : null;

            case ReportedContentTypes.MenuItem:
                var dish = await db.MenuItems.AsNoTracking()
                    .Where(item => item.menu_item_id == draft.ContentId)
                    .Select(item => new { item.availability_status, Owner = item.storefront.registration.vendor.user_id })
                    .SingleOrDefaultAsync(ct);
                return dish is not null && dish.availability_status is not ("HIDDEN" or "ARCHIVED")
                    ? dish.Owner
                    : null;

            default:
                return null;
        }
    }

    private Task<bool> IsHiddenAsync(ContentReportDraft draft, CancellationToken ct) =>
        db.ReportedContents.AnyAsync(
            report => report.content_type == draft.ContentType
                && report.content_id == draft.ContentId
                && report.status == ReportedContentStatuses.Hidden,
            ct);
}
