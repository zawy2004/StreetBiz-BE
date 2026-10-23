using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.AiMarketplace;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Persistence.Repositories;

/// <summary>Read models behind the AI marketplace and report-assist features (AIP-01..04, AIB-01).</summary>
public sealed class AiMarketplaceRepository(StreetBizDbContext db, CommerceRepository commerce, TimeProvider? clock = null)
    : IAiMarketplaceRepository
{
    private const int MinDishesForReferencePrice = 3;

    public async Task<IReadOnlyList<AiCategoryRow>> ListCategoriesAsync(CancellationToken cancellationToken) =>
        await db.FoodCategories.AsNoTracking()
            .OrderBy(category => category.category_name)
            .Select(category => new AiCategoryRow(category.category_id, category.category_name))
            .ToListAsync(cancellationToken);

    public async Task<CategoryPriceRow?> GetCategoryPriceAsync(int categoryId, CancellationToken cancellationToken)
    {
        // Only dishes a customer could buy today count: the same set the marketplace shows.
        var prices = await commerce.SellableMenuItems()
            .Where(item => item.category_id == categoryId)
            .Select(item => item.unit_price)
            .ToListAsync(cancellationToken);
        if (prices.Count < MinDishesForReferencePrice)
        {
            return null;
        }

        prices.Sort();
        var middle = prices.Count / 2;
        var median = prices.Count % 2 == 1 ? prices[middle] : (prices[middle - 1] + prices[middle]) / 2;
        return new CategoryPriceRow(prices.Count, prices[0], median, prices[^1]);
    }

    public async Task<StorefrontReviewsRow?> GetReviewsForSummaryAsync(
        long storefrontId,
        int take,
        CancellationToken cancellationToken)
    {
        var name = await commerce.PublicStorefronts()
            .Where(storefront => storefront.storefront_id == storefrontId)
            .Select(storefront => storefront.storefront_name)
            .SingleOrDefaultAsync(cancellationToken);
        if (name is null)
        {
            return null;
        }

        // A review a moderator hid (ADM-05) is not shown to customers, so it is not summarised either.
        var hidden = db.ReportedContents.AsNoTracking()
            .Where(report => report.content_type == ReportedContentTypes.Review
                && report.status == ReportedContentStatuses.Hidden)
            .Select(report => report.content_id);
        var written = db.Reviews.AsNoTracking()
            .Where(review => review.order.storefront_id == storefrontId
                && review.review_text != null
                && review.review_text != string.Empty
                && !hidden.Contains(review.review_id));
        var total = await written.CountAsync(cancellationToken);
        var rows = await written
            .OrderByDescending(review => review.created_at)
            .ThenByDescending(review => review.review_id)
            .Take(take)
            .Select(review => new ReviewForSummaryRow(
                review.review_id, review.rating, review.review_text!, review.created_at, review.edited_at))
            .ToListAsync(cancellationToken);
        return new StorefrontReviewsRow(name, rows, total);
    }

    public async Task<VendorReportContextRow?> GetVendorReportAsync(long reportId, CancellationToken cancellationToken) =>
        (await ReportRows(db.VendorReports.AsNoTracking().Where(report => report.report_id == reportId), cancellationToken))
        .SingleOrDefault();

    public async Task<IReadOnlyList<VendorReportContextRow>> ListVendorReportsForWardAsync(
        int wardId,
        VendorReportListFilter filter,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var reports = VendorReportRecords.InWard(db, wardId);
        if (filter.Status is not null)
        {
            reports = reports.Where(report => report.report_status == filter.Status);
        }

        if (filter.Type is not null)
        {
            reports = reports.Where(report => report.report_type == filter.Type);
        }

        var now = filter.NowUtc;
        if (filter.Overdue is { } overdue)
        {
            reports = overdue
                ? reports.Where(report => report.report_status == VendorReportStatuses.Pending
                    && report.due_at != null && report.due_at < now)
                : reports.Where(report => !(report.report_status == VendorReportStatuses.Pending
                    && report.due_at != null && report.due_at < now));
        }

        // Overdue first, then what falls due soonest, then the reports already closed, newest first.
        var ordered = reports
            .OrderBy(report => report.report_status == VendorReportStatuses.Pending && report.due_at != null && report.due_at < now
                ? 0
                : report.report_status == VendorReportStatuses.Pending ? 1 : 2)
            .ThenBy(report => report.report_status == VendorReportStatuses.Pending && report.due_at == null ? 1 : 0)
            .ThenBy(report => report.report_status == VendorReportStatuses.Pending ? report.due_at : null)
            .ThenByDescending(report => report.created_at)
            .ThenByDescending(report => report.report_id);
        return await ReportRows(
            ordered.Skip((page - 1) * pageSize).Take(pageSize),
            cancellationToken);
    }

    public Task<bool> ReportEvidenceBelongsToWardAsync(string evidenceUrl, int wardId, CancellationToken cancellationToken) =>
        VendorReportRecords.InWard(db, wardId).AnyAsync(report => report.evidence_url == evidenceUrl, cancellationToken);

    public async Task SetReportLocationAsync(long reportId, string location, CancellationToken cancellationToken)
    {
        var report = await db.VendorReports.SingleOrDefaultAsync(item => item.report_id == reportId, cancellationToken);
        if (report is null)
        {
            return;
        }

        report.ai_extracted_location = location.Length <= 200 ? location : location[..200];
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<VendorReportContextRow>> ReportRows(
        IQueryable<VendorReport> reports,
        CancellationToken cancellationToken)
    {
        var heads = await reports.Select(report => new
        {
            report.report_id,
            report.report_reason,
            report.evidence_url,
            report.report_status,
            report.created_at,
            report.ai_extracted_location,
            report.vendor_id,
            report.slot_id,
            SlotCode = report.slot != null ? report.slot.slot_code : null,
            SlotLat = report.slot != null ? (decimal?)report.slot.latitude : null,
            SlotLon = report.slot != null ? (decimal?)report.slot.longitude : null,
            SlotWard = report.slot != null ? (int?)report.slot.zone.ward_unit_id : null,
            report.scanned_permit_id,
            PermitStatus = report.scanned_permit != null ? report.scanned_permit.permit_status : null,
            PermitContractStatus = report.scanned_permit != null ? report.scanned_permit.contract.contract_status : null,
            PermitSlotId = report.scanned_permit != null ? (long?)report.scanned_permit.contract.slot_id : null,
            PermitSlotCode = report.scanned_permit != null ? report.scanned_permit.contract.slot.slot_code : null,
            PermitWard = report.scanned_permit != null ? (int?)report.scanned_permit.contract.slot.zone.ward_unit_id : null,
            report.report_type,
            report.due_at,
            report.resolution,
            report.resolution_note,
            report.refund_amount,
            report.reviewed_at,
            ReviewerName = report.UserAccount != null ? report.UserAccount.full_name : null,
            report.menu_item_id,
            report.quantity,
            report.posted_amount,
            report.charged_amount,
            report.purchased_at,
            PriceItemName = report.price_history != null ? report.price_history.item_name : null,
            PriceUnit = report.price_history != null ? report.price_history.price_unit : null,
            PriceUnitAmount = report.price_history != null ? (decimal?)report.price_history.unit_price : null,
            PriceChangedAt = report.price_history != null ? (DateTime?)report.price_history.changed_at : null,
            // "Bản số N" of the board the posted price was read from.
            PriceVersion = report.price_history != null
                ? db.MenuItemPriceHistories.Count(row =>
                    row.storefront_id == report.price_history.storefront_id
                    && row.price_history_id <= report.price_history.price_history_id)
                : 0,
            ViolationId = db.Violations
                .Where(violation => violation.source_report_id == report.report_id)
                .Select(violation => (long?)violation.violation_id)
                .FirstOrDefault(),
        }).ToListAsync(cancellationToken);
        if (heads.Count == 0)
        {
            return [];
        }

        // The business the vendor registered under, and the ward of that registration.
        var vendorIds = heads.Where(head => head.vendor_id is not null).Select(head => head.vendor_id!.Value).Distinct().ToArray();
        var registrations = (await db.BusinessRegistrations.AsNoTracking()
                .Where(registration => vendorIds.Contains(registration.vendor_id))
                .OrderByDescending(registration => registration.registration_id)
                .Select(registration => new { registration.vendor_id, registration.display_name, registration.ward_unit_id })
                .ToListAsync(cancellationToken))
            .GroupBy(registration => registration.vendor_id)
            .ToDictionary(group => group.Key, group => group.First());

        // Context for the officer only ("n phản ánh trong 90 ngày"): never a score, never acted on alone.
        var since = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime.AddDays(-VendorReportRules.VendorHistoryDays);
        var recent = (await db.VendorReports.AsNoTracking()
                .Where(report => report.vendor_id != null && vendorIds.Contains(report.vendor_id.Value) && report.created_at >= since)
                .Select(report => report.vendor_id!.Value)
                .ToListAsync(cancellationToken))
            .GroupBy(vendor => vendor)
            .ToDictionary(group => group.Key, group => group.Count());

        return heads.Select(head =>
        {
            registrations.TryGetValue(head.vendor_id ?? 0, out var registration);
            var wards = new[] { head.SlotWard, head.PermitWard, registration?.ward_unit_id }
                .Where(ward => ward is not null)
                .Select(ward => ward!.Value)
                .Distinct()
                .ToArray();
            var evidence = head.menu_item_id is { } item && head.PriceUnitAmount is { } unitPrice
                && head.quantity is { } quantity && head.posted_amount is { } posted
                && head.charged_amount is { } charged && head.purchased_at is { } purchased
                    ? new VendorReportPriceEvidenceRow(item, head.PriceItemName!, head.PriceUnit!, unitPrice,
                        head.PriceVersion, head.PriceChangedAt!.Value, quantity, posted, charged, purchased)
                    : null;
            return new VendorReportContextRow(
                head.report_id,
                head.report_reason,
                head.evidence_url,
                head.report_status,
                head.created_at,
                head.ai_extracted_location,
                registration?.display_name,
                head.vendor_id,
                head.slot_id,
                head.SlotCode,
                (double?)head.SlotLat,
                (double?)head.SlotLon,
                head.scanned_permit_id,
                head.PermitStatus,
                head.PermitContractStatus,
                head.PermitSlotCode,
                head.PermitSlotId,
                wards,
                head.report_type,
                head.due_at,
                head.resolution is { } resolution
                    ? new VendorReportOutcomeRow(resolution, head.resolution_note, head.refund_amount, head.reviewed_at, head.ReviewerName)
                    : null,
                evidence,
                head.vendor_id is { } vendor && recent.TryGetValue(vendor, out var count) ? count : 0,
                head.ViolationId);
        }).ToArray();
    }
}
