using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.CommunityVendors;
using StreetBiz.Application.Features.WardSlots;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Persistence;

/// <summary>
/// Where a citizen report belongs and how it is closed (BUY-05/07, WARD-19), shared by the ward's
/// inbox, the resolve endpoint, recording a violation from a report and the ward dashboard.
/// </summary>
internal static class VendorReportRecords
{
    /// <summary>
    /// A report belongs to a ward through the slot it names, the slot of the permit that was scanned, or the
    /// ward the reported vendor registered in.
    /// </summary>
    public static IQueryable<VendorReport> InWard(StreetBizDbContext db, int wardId) =>
        db.VendorReports.AsNoTracking().Where(report =>
            (report.slot != null && report.slot.zone.ward_unit_id == wardId)
            || (report.scanned_permit != null && report.scanned_permit.contract.slot.zone.ward_unit_id == wardId)
            || (report.vendor != null && db.BusinessRegistrations.Any(registration =>
                registration.vendor_id == report.vendor.vendor_id && registration.ward_unit_id == wardId)));

    /// <summary>The wards a new report belongs to, by the same three routes as <see cref="InWard"/>.</summary>
    public static async Task<IReadOnlyList<int>> WardIdsAsync(
        StreetBizDbContext db, long? vendorId, long? slotId, long? permitId, CancellationToken cancellationToken)
    {
        var wards = new HashSet<int>();
        if (vendorId is { } vendor)
        {
            wards.UnionWith(await db.BusinessRegistrations.AsNoTracking()
                .Where(registration => registration.vendor_id == vendor)
                .Select(registration => registration.ward_unit_id)
                .ToListAsync(cancellationToken));
        }

        if (slotId is { } slot && await db.SidewalkSlots.AsNoTracking()
                .Where(row => row.slot_id == slot).Select(row => (int?)row.zone.ward_unit_id)
                .SingleOrDefaultAsync(cancellationToken) is { } slotWard)
        {
            wards.Add(slotWard);
        }

        if (permitId is { } permit && await db.DigitalPermits.AsNoTracking()
                .Where(row => row.permit_id == permit).Select(row => (int?)row.contract.slot.zone.ward_unit_id)
                .SingleOrDefaultAsync(cancellationToken) is { } permitWard)
        {
            wards.Add(permitWard);
        }

        return wards.ToArray();
    }

    /// <summary>
    /// The stall a report is about, as people know it: the storefront of the dish an overcharge
    /// names, else the registration of the scanned permit, else the vendor's latest approved (else
    /// latest) registration; plus the slot when known.
    /// </summary>
    public static async Task<(string? Name, string? SlotCode)> SubjectAsync(
        StreetBizDbContext db, VendorReport report, CancellationToken cancellationToken)
    {
        string? name = null;
        string? slotCode = null;
        if (report.menu_item_id is { } item)
        {
            var store = await db.MenuItems.AsNoTracking().Where(row => row.menu_item_id == item)
                .Select(row => new { row.storefront.storefront_name, row.storefront.contract.slot.slot_code })
                .SingleOrDefaultAsync(cancellationToken);
            name = store?.storefront_name;
            slotCode = store?.slot_code;
        }

        if (name is null && report.scanned_permit_id is { } permit)
        {
            var scanned = await db.DigitalPermits.AsNoTracking().Where(row => row.permit_id == permit)
                .Select(row => new { row.contract.application.registration.display_name, row.contract.slot.slot_code })
                .SingleOrDefaultAsync(cancellationToken);
            name = scanned?.display_name;
            slotCode ??= scanned?.slot_code;
        }

        if (name is null && report.vendor_id is { } vendor)
        {
            name = await db.BusinessRegistrations.AsNoTracking()
                .Where(row => row.vendor_id == vendor)
                .OrderByDescending(row => row.registration_status == RegistrationStatuses.Approved)
                .ThenByDescending(row => row.registration_id)
                .Select(row => row.display_name)
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (slotCode is null && report.slot_id is { } slot)
        {
            slotCode = await db.SidewalkSlots.AsNoTracking().Where(row => row.slot_id == slot)
                .Select(row => row.slot_code).SingleOrDefaultAsync(cancellationToken);
        }

        return (name, slotCode);
    }

    /// <summary>
    /// Closes a tracked PENDING report as the officer decided: the outcome, who and when (BR-46), a
    /// VENDOR_REPORT_RESOLVED audit row, and the notices to the reporter (with the note, written for
    /// them) and to the vendor (outcome only: never the reporter, never the note). The caller has
    /// checked the ward and the expected status inside its serializable transaction and saves.
    /// </summary>
    public static async Task CloseAsync(
        StreetBizDbContext db,
        VendorReport report,
        string resolution,
        string note,
        decimal? refundAmount,
        WardActor actor,
        DateTime nowUtc,
        long? violationId,
        CancellationToken cancellationToken)
    {
        var expectedStatus = report.report_status;
        report.report_status = VendorReportResolutions.StatusFor(resolution);
        report.resolution = resolution;
        report.resolution_note = note;
        report.refund_amount = refundAmount;
        report.reviewed_by = actor.UserId;
        report.reviewer_role = RoleCodes.WardAuthority;
        report.reviewed_at = nowUtc;

        db.AuditLogs.Add(new AuditLog
        {
            actor_user_id = actor.UserId,
            action = "VENDOR_REPORT_RESOLVED",
            entity_type = VendorReportNotificationTypes.EntityType,
            entity_id = report.report_id,
            details = JsonSerializer.Serialize(new { expectedStatus, resolution, refundAmount, violationId }),
            created_at = nowUtc,
        });

        var (name, slotCode) = await SubjectAsync(db, report, cancellationToken);
        var subject = VendorReportNotices.Subject(name, slotCode);
        Notify(db, report.reporter_user_id, VendorReportNotices.ResolvedForReporter(resolution, subject, note, refundAmount),
            report.report_id, nowUtc);
        if (report.vendor_id is { } vendorId
            && await db.Vendors.AsNoTracking().Where(vendor => vendor.vendor_id == vendorId)
                .Select(vendor => (long?)vendor.user_id).SingleOrDefaultAsync(cancellationToken) is { } vendorUserId)
        {
            Notify(db, vendorUserId, VendorReportNotices.ResolvedForVendor(resolution, subject, refundAmount),
                report.report_id, nowUtc);
        }
    }

    public static void Notify(
        StreetBizDbContext db,
        long userId,
        StreetBiz.Application.Common.Models.NotificationText text,
        long reportId,
        DateTime nowUtc,
        string type = VendorReportNotificationTypes.Resolved) =>
        db.Notifications.Add(new Notification
        {
            user_id = userId,
            notification_type = type,
            title = text.Title,
            body = text.Body,
            related_entity_type = VendorReportNotificationTypes.EntityType,
            related_entity_id = reportId,
            is_read = false,
            sent_at = nowUtc,
        });
}
