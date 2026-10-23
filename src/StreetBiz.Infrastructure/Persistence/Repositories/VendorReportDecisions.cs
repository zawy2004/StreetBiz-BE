using System.Data;
using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.CommunityVendors;
using StreetBiz.Application.Features.VendorReports;
using StreetBiz.Application.Features.WardSlots;

namespace StreetBiz.Infrastructure.Persistence.Repositories;

/// <summary>
/// WARD-19: an officer closes a citizen report. Serializable with an expected status, the WardRules
/// pattern, so two officers closing the same report at once cannot both succeed: the second one
/// finds the status moved and gets 409.
/// </summary>
public sealed class VendorReportDecisions(StreetBizDbContext db, TimeProvider clock) : IVendorReportDecisions
{
    public async Task ResolveAsync(
        WardActor actor,
        long reportId,
        ResolveVendorReportRequest request,
        CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            // Another ward's report answers exactly like a missing one (BR-44/45 scoping).
            if (!await VendorReportRecords.InWard(db, actor.WardId).AnyAsync(report => report.report_id == reportId, cancellationToken))
            {
                throw new NotFoundException(CommunityMessages.ReportNotFound);
            }

            var report = await db.VendorReports.SingleAsync(row => row.report_id == reportId, cancellationToken);
            if (report.report_status != request.ExpectedStatus || report.report_status != VendorReportStatuses.Pending)
            {
                WardRules.Conflict(CommunityMessages.ReportAlreadyClosed);
            }

            if (request.RefundAmount is { } refund)
            {
                if (report.charged_amount is not { } charged)
                {
                    throw new WardException(400, "refund_without_charge",
                        "Chỉ ghi số tiền hoàn cho khiếu nại bán cao hơn giá niêm yết, nơi có số tiền khách bị tính.");
                }

                if (refund > charged)
                {
                    throw new WardException(400, "refund_exceeds_charge",
                        $"Số tiền hoàn không được vượt số tiền khách bị tính ({VendorReportNotices.Money(charged)}).");
                }
            }

            await VendorReportRecords.CloseAsync(
                db, report, request.Outcome, request.Note, request.RefundAmount, actor,
                clock.GetUtcNow().UtcDateTime, violationId: null, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });
    }
}
