using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Exceptions;
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

    public async Task<IReadOnlyList<WardZoneRow>> ListZonesAsync(int wardUnitId, DateOnly today, CancellationToken cancellationToken) =>
        await db.PricingZones.AsNoTracking()
            .Where(zone => zone.ward_unit_id == wardUnitId)
            .Select(zone => new WardZoneRow(
                zone.zone_id,
                zone.zone_name,
                zone.SidewalkSlots.Count(),
                // Rented = a slot under a contract in force today. Nothing moves a contract to
                // EXPIRED when its end date passes, so the dates are checked here, as ordering does.
                zone.SidewalkSlots.Count(slot => slot.RentalContracts.Any(
                    contract => contract.contract_status == ContractStatuses.Active
                        && contract.start_date <= today && contract.end_date >= today))))
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

    public async Task<DateTime?> SendDebtReminderAsync(
        long contractId,
        long vendorUserId,
        long actorUserId,
        string title,
        string body,
        DateTime now,
        DateTime notBeforeUtc,
        CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken);

            // Re-checked inside the write transaction: the handler's own read raced with any
            // other officer pressing "Nhắc nợ" for the same household.
            var earlier = await db.Notifications.AsNoTracking()
                .Where(notification => notification.notification_type == FinanceNotificationTypes.Fee
                    && notification.related_entity_type == ReminderEntityType
                    && notification.related_entity_id == contractId
                    && notification.sent_at >= notBeforeUtc)
                .OrderBy(notification => notification.sent_at)
                .Select(notification => (DateTime?)notification.sent_at)
                .FirstOrDefaultAsync(cancellationToken);
            if (earlier is not null)
            {
                return DateTime.SpecifyKind(earlier.Value, DateTimeKind.Utc);
            }

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
            await transaction.CommitAsync(cancellationToken);
            return (DateTime?)null;
        });
    }

    public async Task<IReadOnlyList<WardRefundRow>> ListRefundsAsync(int wardUnitId, CancellationToken cancellationToken)
    {
        var rows = (await RefundRowsAsync(WardRefundPayments(wardUnitId), cancellationToken)).ToList();
        var refundedAt = await RefundedAtAsync(
            rows.Where(row => row.Status == PaymentStatuses.Refunded).Select(row => row.TransactionId).ToArray(),
            cancellationToken);
        return rows
            .Select(row => row with { RefundedAt = refundedAt.TryGetValue(row.TransactionId, out var at) ? at : null })
            .OrderBy(row => row.Status == PaymentStatuses.RefundRequired ? 0 : 1)
            .ThenByDescending(row => row.ReceivedAt)
            .ToList();
    }

    public async Task<WardRefundRow?> MarkRefundedAsync(
        int wardUnitId, long transactionId, long actorUserId, string? note, DateTime now, CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        var marked = await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken);

            var row = (await RefundRowsAsync(
                    WardRefundPayments(wardUnitId).Where(payment => payment.transaction_id == transactionId),
                    cancellationToken))
                .SingleOrDefault();
            if (row is null)
            {
                return false;
            }

            if (row.Status != PaymentStatuses.RefundRequired)
            {
                throw new DomainRuleException(FinanceMessages.RefundNotRequired);
            }

            var payment = await db.PaymentTransactions.SingleAsync(
                payment => payment.transaction_id == transactionId, cancellationToken);
            payment.transaction_status = PaymentStatuses.Refunded;

            db.AuditLogs.Add(new AuditLog
            {
                actor_user_id = actorUserId,
                action = RefundedAction,
                entity_type = "PaymentTransaction",
                entity_id = transactionId,
                details = JsonSerializer.Serialize(new { row.Amount, row.Provider, row.ProviderReference, note }),
                created_at = now,
            });
            db.Notifications.Add(new Notification
            {
                user_id = row.VendorUserId,
                notification_type = row.Kind == "FEE" ? FinanceNotificationTypes.Fee : FinanceNotificationTypes.Penalty,
                title = "Đã hoàn tiền thanh toán trùng",
                body = $"UBND phường đã hoàn lại {row.Amount.ToString("#,##0", Vietnamese)} đ bạn thanh toán trùng cho {row.Label}.",
                related_entity_type = "PaymentTransaction",
                related_entity_id = transactionId,
                sent_at = now,
            });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        });

        return marked
            ? (await ListRefundsAsync(wardUnitId, cancellationToken)).Single(row => row.TransactionId == transactionId)
            : null;
    }

    private const string RefundedAction = "FINANCE_PAYMENT_REFUNDED";

    private static readonly CultureInfo Vietnamese = CultureInfo.GetCultureInfo("vi-VN");

    /// <summary>Owed-back and refunded fee/penalty payments of this ward, scoped by the slot's zone like every ward figure.</summary>
    private IQueryable<PaymentTransaction> WardRefundPayments(int wardUnitId) =>
        db.PaymentTransactions.AsNoTracking()
            .Where(payment => (payment.transaction_status == PaymentStatuses.RefundRequired
                    || payment.transaction_status == PaymentStatuses.Refunded)
                && ((payment.fee_item != null && payment.fee_item.fee_schedule.contract.slot.zone.ward_unit_id == wardUnitId)
                    || (payment.penalty != null && payment.penalty.violation.slot != null
                        && payment.penalty.violation.slot.zone.ward_unit_id == wardUnitId)));

    /// <summary>
    /// Two simple projections, fee then penalty, rather than one spanning both navigation chains:
    /// the shape EF Core failed to translate before (see FinanceRepository's payment history).
    /// </summary>
    private async Task<IEnumerable<WardRefundRow>> RefundRowsAsync(
        IQueryable<PaymentTransaction> payments, CancellationToken cancellationToken)
    {
        var fees = await payments
            .Where(payment => payment.fee_item != null)
            .Select(payment => new
            {
                payment.transaction_id,
                VendorUserId = payment.fee_item!.fee_schedule.contract.vendor.user_id,
                Name = payment.fee_item.fee_schedule.contract.vendor.user.full_name
                    ?? payment.fee_item.fee_schedule.contract.vendor.user.phone_number,
                Phone = payment.fee_item.fee_schedule.contract.vendor.user.phone_number,
                SlotCode = payment.fee_item.fee_schedule.contract.slot.slot_code,
                payment.fee_item.due_date,
                payment.provider,
                payment.provider_reference,
                payment.amount,
                ReceivedAt = payment.callback_received_at ?? payment.created_at,
                payment.transaction_status,
            })
            .ToListAsync(cancellationToken);
        var penalties = await payments
            .Where(payment => payment.penalty != null)
            .Select(payment => new
            {
                payment.transaction_id,
                VendorUserId = payment.penalty!.violation.vendor!.user_id,
                Name = payment.penalty.violation.vendor.user.full_name ?? payment.penalty.violation.vendor.user.phone_number,
                Phone = payment.penalty.violation.vendor.user.phone_number,
                SlotCode = payment.penalty.violation.slot != null ? payment.penalty.violation.slot.slot_code : null,
                Label = payment.penalty.violation.violation_typeNavigation.description,
                payment.provider,
                payment.provider_reference,
                payment.amount,
                ReceivedAt = payment.callback_received_at ?? payment.created_at,
                payment.transaction_status,
            })
            .ToListAsync(cancellationToken);

        return fees
            .Select(row => new WardRefundRow(
                row.transaction_id, "FEE", row.VendorUserId, row.Name, row.Phone, row.SlotCode,
                $"phí thuê ô {row.SlotCode} hạn {row.due_date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}",
                row.provider, row.provider_reference, row.amount,
                DateTime.SpecifyKind(row.ReceivedAt, DateTimeKind.Utc), row.transaction_status, null))
            .Concat(penalties.Select(row => new WardRefundRow(
                row.transaction_id, "PENALTY", row.VendorUserId, row.Name, row.Phone, row.SlotCode,
                $"tiền phạt: {row.Label}",
                row.provider, row.provider_reference, row.amount,
                DateTime.SpecifyKind(row.ReceivedAt, DateTimeKind.Utc), row.transaction_status, null)));
    }

    private async Task<IReadOnlyDictionary<long, DateTime>> RefundedAtAsync(
        IReadOnlyCollection<long> transactionIds, CancellationToken cancellationToken)
    {
        if (transactionIds.Count == 0)
        {
            return new Dictionary<long, DateTime>();
        }

        var ids = transactionIds.ToArray();
        return (await db.AuditLogs.AsNoTracking()
                .Where(log => log.action == RefundedAction && log.entity_type == "PaymentTransaction" && ids.Contains(log.entity_id))
                .Select(log => new { log.entity_id, log.created_at })
                .ToListAsync(cancellationToken))
            .GroupBy(log => log.entity_id)
            .ToDictionary(group => group.Key, group => DateTime.SpecifyKind(group.Max(log => log.created_at), DateTimeKind.Utc));
    }
}
