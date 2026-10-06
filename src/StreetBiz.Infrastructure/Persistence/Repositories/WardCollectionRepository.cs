using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Persistence.Repositories;

/// <summary>
/// WARD-14 collections. Plain, ward-scoped row reads; all arithmetic happens in
/// WardCollectionAnalytics, where it is unit-tested. Scoped through the slot's pricing zone, like
/// WardReportRepository, so both screens count the same instalments.
/// </summary>
public sealed class WardCollectionRepository(StreetBizDbContext db) : IWardCollectionRepository
{
    /// <summary>How a ward debt reminder is recognised among a vendor's notifications.</summary>
    public const string ReminderEntityType = "RentalContract";

    public async Task<IReadOnlyList<WardUnpaidItemRow>> ListUnpaidFeeItemsAsync(
        int wardUnitId, CancellationToken cancellationToken) =>
        await db.FeeScheduleItems.AsNoTracking()
            .Where(item => (item.item_status == FeeItemStatuses.Pending || item.item_status == FeeItemStatuses.Overdue)
                && item.fee_schedule.superseded_at == null
                && item.fee_schedule.contract.slot.zone.ward_unit_id == wardUnitId)
            .Select(item => new WardUnpaidItemRow(
                item.fee_item_id,
                item.fee_schedule.contract_id,
                item.fee_schedule.contract.vendor.user_id,
                item.fee_schedule.contract.vendor.user.full_name ?? item.fee_schedule.contract.vendor.user.phone_number,
                item.fee_schedule.contract.vendor.user.phone_number,
                item.fee_schedule.contract.application.registration.display_name,
                item.fee_schedule.contract.slot.zone_id,
                item.fee_schedule.contract.slot.zone.zone_name,
                item.fee_schedule.contract.slot.slot_code,
                item.due_date,
                item.amount))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<WardFeeItemRow>> ListFeeActivityAsync(
        int wardUnitId,
        DateOnly dueFrom,
        DateOnly dueTo,
        DateTime paidFromUtc,
        DateTime paidToExclusiveUtc,
        CancellationToken cancellationToken) =>
        await db.FeeScheduleItems.AsNoTracking()
            .Where(item => item.fee_schedule.contract.slot.zone.ward_unit_id == wardUnitId
                && ((item.due_date >= dueFrom && item.due_date <= dueTo)
                    || (item.paid_at >= paidFromUtc && item.paid_at < paidToExclusiveUtc)))
            .Select(item => new WardFeeItemRow(
                item.fee_schedule.contract.slot.zone_id,
                item.due_date,
                item.amount,
                item.item_status,
                item.paid_at,
                item.fee_schedule.superseded_at == null))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<WardPenaltyPaymentRow>> ListPenaltyPaymentsAsync(
        int wardUnitId, DateTime fromUtc, DateTime toExclusiveUtc, CancellationToken cancellationToken) =>
        await db.Penalties.AsNoTracking()
            .Where(penalty => penalty.penalty_status == PenaltyStatuses.Paid
                && penalty.paid_at >= fromUtc && penalty.paid_at < toExclusiveUtc
                && penalty.violation.slot != null && penalty.violation.slot.zone.ward_unit_id == wardUnitId)
            .Select(penalty => new WardPenaltyPaymentRow(penalty.amount, penalty.paid_at!.Value))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<WardZoneRow>> ListZonesAsync(int wardUnitId, CancellationToken cancellationToken) =>
        await db.PricingZones.AsNoTracking()
            .Where(zone => zone.ward_unit_id == wardUnitId)
            .Select(zone => new WardZoneRow(
                zone.zone_id,
                zone.zone_name,
                zone.SidewalkSlots.Count(),
                // Rented = a slot under an active contract, the same test the fees themselves use.
                zone.SidewalkSlots.Count(slot => slot.RentalContracts.Any(
                    contract => contract.contract_status == ContractStatuses.Active))))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<long, DateTime>> GetLastDebtRemindersAsync(
        IReadOnlyCollection<long> contractIds, CancellationToken cancellationToken)
    {
        if (contractIds.Count == 0)
        {
            return new Dictionary<long, DateTime>();
        }

        var ids = contractIds.ToArray();
        var reminders = await db.Notifications.AsNoTracking()
            .Where(notification => notification.notification_type == FinanceNotificationTypes.Fee
                && notification.related_entity_type == ReminderEntityType
                && notification.related_entity_id != null
                && ids.Contains(notification.related_entity_id.Value))
            .GroupBy(notification => notification.related_entity_id!.Value)
            .Select(group => new { ContractId = group.Key, Last = group.Max(notification => notification.sent_at) })
            .ToListAsync(cancellationToken);
        return reminders.ToDictionary(row => row.ContractId, row => row.Last);
    }

    public async Task SendDebtReminderAsync(
        long contractId,
        long vendorUserId,
        long actorUserId,
        string title,
        string body,
        DateTime now,
        CancellationToken cancellationToken)
    {
        db.Notifications.Add(new Notification
        {
            user_id = vendorUserId,
            notification_type = FinanceNotificationTypes.Fee,
            title = title,
            body = body,
            related_entity_type = ReminderEntityType,
            related_entity_id = contractId,
            sent_at = now,
        });
        // A reminder is a ward officer acting on a household: it belongs in the compliance trail.
        db.AuditLogs.Add(new AuditLog
        {
            actor_user_id = actorUserId,
            action = "FEE_DEBT_REMINDER_SENT",
            entity_type = ReminderEntityType,
            entity_id = contractId,
            details = JsonSerializer.Serialize(new { body }),
            created_at = now,
        });
        await db.SaveChangesAsync(cancellationToken);
    }
}
