using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.Infrastructure.Persistence.Repositories;

/// <summary>
/// WARD-14/WARD-15. Every aggregate here is its own small, separately-executed query rather than
/// one combined projection: an earlier attempt at combining several LEFT JOIN chains into one
/// Select (see the FEE-03 invoice list) hit a query EF Core could not translate. Ten simple round
/// trips are well inside PER-01/PER-03's pilot budget (50 concurrent users, sub-second reads) and
/// SCA-05 does not ask this to scale past one ward's data.
///
/// A violation is scoped to a ward through its slot (violation.slot.zone.ward_unit_id): every
/// seeded violation carries a slot_id, and "where on the sidewalk" is the natural ward locator for
/// a sidewalk-compliance record, unlike vendor_id alone (nullable, and a vendor's registration can
/// span more than one ward via BR-06's multiple storefronts).
/// </summary>
public sealed class WardReportRepository(StreetBizDbContext db) : IWardReportRepository
{
    public async Task<CollectionReportRow> GetCollectionReportAsync(
        int wardUnitId, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var fromUtc = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var toUtc = to.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc);

        var feeCollected = await db.FeeScheduleItems.AsNoTracking()
            .Where(item => item.item_status == FeeItemStatuses.Paid
                && item.paid_at >= fromUtc && item.paid_at <= toUtc
                && item.fee_schedule.contract.slot.zone.ward_unit_id == wardUnitId)
            .SumAsync(item => (decimal?)item.amount, cancellationToken) ?? 0m;

        var feePending = await SlotScopedFeeItems(wardUnitId, FeeItemStatuses.Pending)
            .SumAsync(item => (decimal?)item.amount, cancellationToken) ?? 0m;

        var feeOverdue = await SlotScopedFeeItems(wardUnitId, FeeItemStatuses.Overdue)
            .SumAsync(item => (decimal?)item.amount, cancellationToken) ?? 0m;

        var penaltyCollected = await db.Penalties.AsNoTracking()
            .Where(penalty => penalty.penalty_status == PenaltyStatuses.Paid
                && penalty.paid_at >= fromUtc && penalty.paid_at <= toUtc
                && penalty.violation.slot != null && penalty.violation.slot.zone.ward_unit_id == wardUnitId)
            .SumAsync(penalty => (decimal?)penalty.amount, cancellationToken) ?? 0m;

        var penaltyPending = await SlotScopedPenalties(wardUnitId, PenaltyStatuses.Unpaid)
            .SumAsync(penalty => (decimal?)penalty.amount, cancellationToken) ?? 0m;

        var invoiceCount = await db.Invoices.AsNoTracking()
            .Where(invoice => invoice.issued_at >= fromUtc && invoice.issued_at <= toUtc)
            .Where(invoice =>
                (invoice.fee_item != null && invoice.fee_item.fee_schedule.contract.slot.zone.ward_unit_id == wardUnitId)
                || (invoice.penalty != null && invoice.penalty.violation.slot != null
                    && invoice.penalty.violation.slot.zone.ward_unit_id == wardUnitId))
            .CountAsync(cancellationToken);

        var recentViolations = await db.Violations.AsNoTracking()
            .Where(violation => violation.slot != null && violation.slot.zone.ward_unit_id == wardUnitId)
            .OrderByDescending(violation => violation.recorded_at)
            .Take(10)
            .Select(violation => new RecentViolationRow(
                violation.violation_id,
                violation.violation_type,
                violation.violation_typeNavigation.description,
                violation.vendor != null ? violation.vendor.user.full_name : null,
                violation.slot != null ? violation.slot.slot_code : null,
                violation.Penalty != null ? violation.Penalty.amount : (decimal?)null,
                violation.Penalty != null ? violation.Penalty.penalty_status : null,
                violation.recorded_at))
            .ToListAsync(cancellationToken);

        return new CollectionReportRow(
            feeCollected, feePending, feeOverdue, penaltyCollected, penaltyPending, invoiceCount, recentViolations);
    }

    public async Task<WardDashboardRow> GetDashboardAsync(int wardUnitId, CancellationToken cancellationToken)
    {
        var slotTotal = await db.SidewalkSlots.AsNoTracking()
            .CountAsync(slot => slot.zone.ward_unit_id == wardUnitId, cancellationToken);
        var slotRented = await db.SidewalkSlots.AsNoTracking()
            .CountAsync(slot => slot.zone.ward_unit_id == wardUnitId && slot.slot_status == SlotStatuses.Active, cancellationToken);

        var pendingRegistrations = await db.BusinessRegistrations.AsNoTracking()
            .CountAsync(registration => registration.ward_unit_id == wardUnitId
                && (registration.registration_status == RegistrationStatuses.Submitted
                    || registration.registration_status == RegistrationStatuses.UnderReview
                    || registration.registration_status == RegistrationStatuses.MoreInformationRequired),
                cancellationToken);

        var pendingApplications = await db.RentalApplications.AsNoTracking()
            .CountAsync(application => application.slot.zone.ward_unit_id == wardUnitId
                && application.application_status == ApplicationStatuses.Pending,
                cancellationToken);

        var activeContracts = await db.RentalContracts.AsNoTracking()
            .CountAsync(contract => contract.slot.zone.ward_unit_id == wardUnitId
                && contract.contract_status == ContractStatuses.Active,
                cancellationToken);

        var feeRevenue = await SlotScopedFeeItems(wardUnitId, FeeItemStatuses.Paid)
            .SumAsync(item => (decimal?)item.amount, cancellationToken) ?? 0m;
        var penaltyRevenue = await SlotScopedPenalties(wardUnitId, PenaltyStatuses.Paid)
            .SumAsync(penalty => (decimal?)penalty.amount, cancellationToken) ?? 0m;

        var feeOutstanding = await db.FeeScheduleItems.AsNoTracking()
            .Where(item => item.item_status == FeeItemStatuses.Pending || item.item_status == FeeItemStatuses.Overdue)
            .Where(item => item.fee_schedule.contract.slot.zone.ward_unit_id == wardUnitId)
            .SumAsync(item => (decimal?)item.amount, cancellationToken) ?? 0m;
        var penaltyOutstanding = await SlotScopedPenalties(wardUnitId, PenaltyStatuses.Unpaid)
            .SumAsync(penalty => (decimal?)penalty.amount, cancellationToken) ?? 0m;

        // "Open" = still needs ward attention: no penalty issued yet, or one issued but unpaid.
        var openViolations = await db.Violations.AsNoTracking()
            .CountAsync(violation => violation.slot != null && violation.slot.zone.ward_unit_id == wardUnitId
                && (violation.Penalty == null || violation.Penalty.penalty_status == PenaltyStatuses.Unpaid),
                cancellationToken);

        return new WardDashboardRow(
            slotTotal,
            slotRented,
            pendingRegistrations,
            pendingApplications,
            activeContracts,
            feeRevenue + penaltyRevenue,
            feeOutstanding + penaltyOutstanding,
            openViolations);
    }

    private IQueryable<ScaffoldedModels.FeeScheduleItem> SlotScopedFeeItems(int wardUnitId, string status) =>
        db.FeeScheduleItems.AsNoTracking()
            .Where(item => item.item_status == status && item.fee_schedule.contract.slot.zone.ward_unit_id == wardUnitId);

    private IQueryable<ScaffoldedModels.Penalty> SlotScopedPenalties(int wardUnitId, string status) =>
        db.Penalties.AsNoTracking()
            .Where(penalty => penalty.penalty_status == status
                && penalty.violation.slot != null && penalty.violation.slot.zone.ward_unit_id == wardUnitId);
}
