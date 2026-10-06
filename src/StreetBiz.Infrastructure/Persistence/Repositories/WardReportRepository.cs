using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Finance;

namespace StreetBiz.Infrastructure.Persistence.Repositories;

/// <summary>
/// WARD-14/WARD-15. Every aggregate here is its own small, separately-executed query rather than
/// one combined projection: an earlier attempt at combining several LEFT JOIN chains into one
/// Select (see the FEE-03 invoice list) hit a query EF Core could not translate. A dozen simple
/// round trips are well inside PER-01/PER-03's pilot budget (50 concurrent users, sub-second reads) and
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
        // The period is Vietnamese calendar days: [00:00 on `from`, 00:00 the day after `to`) in
        // Đà Nẵng time, expressed in UTC to match paid_at/issued_at. UTC midnights would push a
        // payment made at 03:00 on the 1st into the previous month.
        var fromUtc = BusinessCalendar.StartOfDayUtc(from);
        var toExclusiveUtc = BusinessCalendar.StartOfDayUtc(to.AddDays(1));

        var feeCollected = await db.FeeScheduleItems.AsNoTracking()
            .Where(item => item.item_status == FeeItemStatuses.Paid
                && item.paid_at >= fromUtc && item.paid_at < toExclusiveUtc
                && item.fee_schedule.contract.slot.zone.ward_unit_id == wardUnitId)
            .SumAsync(item => (decimal?)item.amount, cancellationToken) ?? 0m;

        var feePending = await OwedFeeItems(wardUnitId, FeeItemStatuses.Pending)
            .SumAsync(item => (decimal?)item.amount, cancellationToken) ?? 0m;

        var feeOverdue = await OwedFeeItems(wardUnitId, FeeItemStatuses.Overdue)
            .SumAsync(item => (decimal?)item.amount, cancellationToken) ?? 0m;

        var penaltyCollected = await db.Penalties.AsNoTracking()
            .Where(penalty => penalty.penalty_status == PenaltyStatuses.Paid
                && penalty.paid_at >= fromUtc && penalty.paid_at < toExclusiveUtc
                && penalty.violation.slot != null && penalty.violation.slot.zone.ward_unit_id == wardUnitId)
            .SumAsync(penalty => (decimal?)penalty.amount, cancellationToken) ?? 0m;

        var penaltyPending = await SlotScopedPenalties(wardUnitId, PenaltyStatuses.Unpaid)
            .SumAsync(penalty => (decimal?)penalty.amount, cancellationToken) ?? 0m;

        var invoiceCount = await db.Invoices.AsNoTracking()
            .Where(invoice => invoice.issued_at >= fromUtc && invoice.issued_at < toExclusiveUtc)
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

        var feeRevenue = await WardFeeItems(wardUnitId)
            .Where(item => item.item_status == FeeItemStatuses.Paid)
            .SumAsync(item => (decimal?)item.amount, cancellationToken) ?? 0m;
        var penaltyRevenue = await SlotScopedPenalties(wardUnitId, PenaltyStatuses.Paid)
            .SumAsync(penalty => (decimal?)penalty.amount, cancellationToken) ?? 0m;

        var feeOutstanding = (await OwedFeeItems(wardUnitId, FeeItemStatuses.Pending)
                .SumAsync(item => (decimal?)item.amount, cancellationToken) ?? 0m)
            + (await OwedFeeItems(wardUnitId, FeeItemStatuses.Overdue)
                .SumAsync(item => (decimal?)item.amount, cancellationToken) ?? 0m);
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

    /// <summary>
    /// Fee and penalty receipts are read by two separate queries and merged in memory, for the
    /// same reason as everything else here; penalties are placed in a ward through the
    /// violation's slot, as the report's own penalty totals are.
    /// </summary>
    public async Task<IReadOnlyList<WardInvoiceRow>> ListWardInvoicesAsync(
        int wardUnitId, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var fromUtc = BusinessCalendar.StartOfDayUtc(from);
        var toExclusiveUtc = BusinessCalendar.StartOfDayUtc(to.AddDays(1));

        var fees = await db.Invoices.AsNoTracking()
            .Where(invoice => invoice.fee_item != null
                && invoice.issued_at >= fromUtc && invoice.issued_at < toExclusiveUtc
                && invoice.fee_item.fee_schedule.contract.slot.zone.ward_unit_id == wardUnitId)
            .Select(invoice => new
            {
                invoice.invoice_number,
                invoice.issued_at,
                invoice.amount,
                Payer = invoice.vendor.user.full_name,
                SlotCode = invoice.fee_item!.fee_schedule.contract.slot.slot_code,
                invoice.fee_item.due_date,
                Ordinal = invoice.fee_item.fee_schedule.FeeScheduleItems
                    .Count(sibling => sibling.due_date < invoice.fee_item.due_date) + 1,
                OfCount = invoice.fee_item.fee_schedule.FeeScheduleItems.Count(),
                Provider = invoice.fee_item.PaymentTransactions
                    .Where(payment => payment.transaction_status == PaymentStatuses.Success)
                    .Select(payment => payment.provider)
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        var penalties = await db.Invoices.AsNoTracking()
            .Where(invoice => invoice.penalty != null
                && invoice.issued_at >= fromUtc && invoice.issued_at < toExclusiveUtc
                && invoice.penalty.violation.slot != null
                && invoice.penalty.violation.slot.zone.ward_unit_id == wardUnitId)
            .Select(invoice => new
            {
                invoice.invoice_number,
                invoice.issued_at,
                invoice.amount,
                Payer = invoice.vendor.user.full_name,
                SlotCode = invoice.penalty!.violation.slot!.slot_code,
                ViolationLabel = invoice.penalty.violation.violation_typeNavigation.description,
                invoice.penalty.decision_number,
                Provider = invoice.penalty.PaymentTransactions
                    .Where(payment => payment.transaction_status == PaymentStatuses.Success)
                    .Select(payment => payment.provider)
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        return fees
            .Select(row => new WardInvoiceRow(
                row.invoice_number,
                DateTime.SpecifyKind(row.issued_at, DateTimeKind.Utc),
                "FEE",
                row.Payer,
                row.SlotCode,
                $"Phí thuê ô {row.SlotCode} — {FinanceMapper.PeriodLabel(row.Ordinal, row.OfCount, row.due_date)}",
                row.amount,
                row.Provider))
            .Concat(penalties.Select(row => new WardInvoiceRow(
                row.invoice_number,
                DateTime.SpecifyKind(row.issued_at, DateTimeKind.Utc),
                "PENALTY",
                row.Payer,
                row.SlotCode,
                row.decision_number is null
                    ? $"Tiền phạt: {row.ViolationLabel}"
                    : $"Tiền phạt: {row.ViolationLabel} (QĐ {row.decision_number})",
                row.amount,
                row.Provider)))
            .OrderBy(row => row.IssuedAt)
            .ThenBy(row => row.InvoiceNumber, StringComparer.Ordinal)
            .ToList();
    }

    public Task<string?> GetWardNameAsync(int wardUnitId, CancellationToken cancellationToken) =>
        db.AdministrativeUnits.AsNoTracking()
            .Where(unit => unit.unit_id == wardUnitId)
            .Select(unit => unit.unit_name)
            .FirstOrDefaultAsync(cancellationToken);

    private IQueryable<ScaffoldedModels.FeeScheduleItem> WardFeeItems(int wardUnitId) =>
        db.FeeScheduleItems.AsNoTracking()
            .Where(item => item.fee_schedule.contract.slot.zone.ward_unit_id == wardUnitId);

    /// <summary>
    /// Instalments still owed. A superseded revision keeps its items as PENDING/OVERDUE (the
    /// CHECK constraint has no "superseded" status), so counting them would bill the vendor twice
    /// for the same period. Paid items are never filtered this way: money received stays revenue.
    /// </summary>
    private IQueryable<ScaffoldedModels.FeeScheduleItem> OwedFeeItems(int wardUnitId, string status) =>
        WardFeeItems(wardUnitId)
            .Where(item => item.item_status == status && item.fee_schedule.superseded_at == null);

    private IQueryable<ScaffoldedModels.Penalty> SlotScopedPenalties(int wardUnitId, string status) =>
        db.Penalties.AsNoTracking()
            .Where(penalty => penalty.penalty_status == status
                && penalty.violation.slot != null && penalty.violation.slot.zone.ward_unit_id == wardUnitId);
}
