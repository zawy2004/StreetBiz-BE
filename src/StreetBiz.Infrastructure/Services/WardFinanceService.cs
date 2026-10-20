using System.Data;
using System.Globalization;
using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.WardFinance;
using StreetBiz.Application.Features.WardSlots;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Services;

/// <summary>
/// The ward's hands on the money after the automatic pricing has run: correct or waive one
/// instalment, waive a penalty, record a payment taken in person, price one slot by hand, hand a
/// deposit back. Every write needs a reason, leaves an audit row naming the officer and tells the
/// vendor. Scoping is by ward through the contract's slot's zone, same as the other ward services.
/// </summary>
public sealed class WardFinanceService(
    StreetBizDbContext db,
    IFinanceRepository finance,
    TimeProvider clock) : IWardFinanceService
{
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private DateOnly TodayVn =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(Now, BusinessCalendar.TimeZone));

    private static string Vnd(decimal amount) => $"{amount.ToString("N0", Vi)} đ";

    /// <summary>A violation has no ward column: its slot's ward, else its contract's slot's,
    /// else the recording officer's (the same rule as WardComplianceService.InWard).</summary>
    private static Expression<Func<Violation, bool>> ViolationInWard(int wardId) => v =>
        (v.slot_id != null && v.slot!.zone.ward_unit_id == wardId)
        || (v.slot_id == null && v.contract_id != null && v.contract!.slot.zone.ward_unit_id == wardId)
        || (v.slot_id == null && v.contract_id == null && v.UserAccount!.ward_unit_id == wardId);

    private async Task Write(Func<Task> action, CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            await action();
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        });
    }

    // ------------------------------------------------------------------ reads

    public async Task<IReadOnlyList<WardFinanceContractListItemDto>> ListContractsAsync(WardActor actor, CancellationToken ct)
    {
        var contracts = await db.RentalContracts.AsNoTracking()
            .Where(c => c.slot.zone.ward_unit_id == actor.WardId)
            .OrderByDescending(c => c.contract_id)
            .Select(c => new
            {
                c.contract_id,
                SlotCode = c.slot.slot_code,
                ZoneName = c.slot.zone.zone_name,
                c.vendor_id,
                VendorName = c.application.registration.display_name,
                Phone = c.vendor.user.phone_number,
                c.contract_status,
                c.start_date,
                c.end_date,
            })
            .ToListAsync(ct);
        if (contracts.Count == 0)
        {
            return [];
        }

        var contractIds = contracts.Select(c => c.contract_id).ToList();
        var vendorIds = contracts.Select(c => c.vendor_id).Distinct().ToList();

        var items = await db.FeeScheduleItems.AsNoTracking()
            .Where(i => contractIds.Contains(i.fee_schedule.contract_id))
            .Select(i => new
            {
                i.fee_schedule.contract_id,
                Open = i.fee_schedule.superseded_at == null,
                i.amount,
                i.item_status,
                i.deposit_amount,
            })
            .ToListAsync(ct);
        var penalties = await db.Penalties.AsNoTracking()
            .Where(p => p.penalty_status == PenaltyStatuses.Unpaid
                && p.violation.vendor_id != null && vendorIds.Contains(p.violation.vendor_id.Value))
            .Select(p => new { VendorId = p.violation.vendor_id!.Value, p.amount })
            .ToListAsync(ct);
        var refunded = (await db.DepositRefunds.AsNoTracking()
            .Where(r => contractIds.Contains(r.contract_id))
            .Select(r => r.contract_id)
            .ToListAsync(ct)).ToHashSet();

        return contracts.Select(c =>
        {
            var mine = items.Where(i => i.contract_id == c.contract_id).ToList();
            var outstanding = mine.Where(i => i.Open && FeeItemStatuses.IsOutstanding(i.item_status)).ToList();
            return new WardFinanceContractListItemDto(
                c.contract_id, c.SlotCode, c.ZoneName, c.VendorName, c.Phone, c.contract_status,
                c.start_date, c.end_date,
                outstanding.Sum(i => i.amount),
                outstanding.Count(i => i.item_status == FeeItemStatuses.Overdue),
                penalties.Where(p => p.VendorId == c.vendor_id).Sum(p => p.amount),
                mine.Where(i => i.item_status == FeeItemStatuses.Paid).Sum(i => Math.Min(i.deposit_amount, i.amount)),
                refunded.Contains(c.contract_id));
        }).ToList();
    }

    public async Task<WardFinanceContractDetailDto> GetContractAsync(WardActor actor, long contractId, CancellationToken ct)
    {
        var contract = await db.RentalContracts.AsNoTracking()
            .Include(c => c.slot).ThenInclude(s => s.zone)
            .Include(c => c.application).ThenInclude(a => a.registration)
            .Include(c => c.vendor).ThenInclude(v => v.user)
            .SingleOrDefaultAsync(c => c.contract_id == contractId && c.slot.zone.ward_unit_id == actor.WardId, ct)
            ?? throw new NotFoundException(FinanceMessages.ContractNotFound);

        var allItems = await db.FeeScheduleItems.AsNoTracking()
            .Include(i => i.fee_schedule)
            .Where(i => i.fee_schedule.contract_id == contractId)
            .OrderBy(i => i.due_date).ThenBy(i => i.fee_item_id)
            .ToListAsync(ct);
        var open = allItems.Where(i => i.fee_schedule.superseded_at == null).ToList();
        var schedule = open.FirstOrDefault()?.fee_schedule;

        var items = open.Select((i, index) => new WardFeeItemDto(
            i.fee_item_id, index + 1, i.due_date, i.amount, Math.Min(i.deposit_amount, i.amount), i.item_status, i.paid_at,
            i.original_amount, i.adjusted_at, i.adjustment_reason,
            CanEdit: FeeItemStatuses.IsOutstanding(i.item_status))).ToList();

        var penalties = await db.Penalties.AsNoTracking()
            .Where(p => p.violation.vendor_id == contract.vendor_id)
            .Where(p => db.Violations.Where(ViolationInWard(actor.WardId)).Select(v => v.violation_id).Contains(p.violation_id))
            .OrderByDescending(p => p.created_at)
            .Select(p => new WardPenaltyItemDto(
                p.penalty_id, p.violation_id, p.violation.violation_typeNavigation.description, p.amount,
                p.penalty_status, p.decision_number, p.created_at, p.paid_at, p.waiver_reason))
            .ToListAsync(ct);

        var depositPaid = allItems.Where(i => i.item_status == FeeItemStatuses.Paid)
            .Sum(i => Math.Min(i.deposit_amount, i.amount));
        var refund = await db.DepositRefunds.AsNoTracking()
            .Where(r => r.contract_id == contractId)
            .Select(r => new WardDepositRefundDto(
                r.deposit_paid, r.amount, r.deduction_reason, r.method, r.reference, r.refunded_at))
            .SingleOrDefaultAsync(ct);

        string? blocked = null;
        if (refund is not null)
        {
            blocked = "Đã hoàn cọc cho hợp đồng này.";
        }
        else if (depositPaid <= 0)
        {
            blocked = "Hợp đồng chưa thu khoản cọc nào.";
        }
        else if (!HasEnded(contract))
        {
            blocked = "Hợp đồng còn hiệu lực. Chỉ hoàn cọc khi hợp đồng đã hết hạn hoặc đã chấm dứt.";
        }

        var zone = contract.slot.zone;
        var pricing = new ZonePricing(zone.price_per_day, zone.price_per_month, zone.rental_mode, zone.quarter_discount_percent);

        return new WardFinanceContractDetailDto(
            contract.contract_id, contract.slot_id, contract.slot.slot_code, zone.zone_name, zone.rental_mode,
            contract.application.registration.display_name, contract.vendor.user.phone_number,
            contract.contract_status, contract.start_date, contract.end_date,
            schedule?.total_amount ?? 0m,
            open.Where(i => FeeItemStatuses.IsOutstanding(i.item_status)).Sum(i => i.amount),
            items, penalties, depositPaid, refund, blocked is null, blocked,
            contract.slot.price_override,
            pricing.BillsByMonth ? pricing.MonthlyPrice : pricing.PricePerDay);
    }

    private bool HasEnded(RentalContract contract) =>
        contract.contract_status is not (ContractStatuses.Active or ContractStatuses.Suspended)
        || contract.end_date < TodayVn;

    // ----------------------------------------------------------------- writes

    public async Task<WardFinanceActionResultDto> AdjustFeeItemAsync(
        WardActor actor, long feeItemId, AdjustFeeItemRequest request, CancellationToken ct)
    {
        string message = "";
        await Write(async () =>
        {
            var item = await LoadOwnedOpenItemAsync(actor, feeItemId, ct);
            var newAmount = request.NewAmount ?? item.amount;
            var newDue = request.NewDueDate ?? item.due_date;
            if (newAmount == item.amount && newDue == item.due_date)
            {
                throw new WardException(400, "no_change", "Số tiền và hạn nộp mới trùng với giá trị hiện tại.");
            }

            var beforeAmount = item.amount;
            var beforeDue = item.due_date;
            item.original_amount ??= item.amount;
            item.amount = newAmount;
            item.deposit_amount = Math.Min(item.deposit_amount, newAmount);
            item.due_date = newDue;
            if (item.item_status == FeeItemStatuses.Overdue && newDue >= TodayVn)
            {
                item.item_status = FeeItemStatuses.Pending;
            }

            item.adjusted_by = actor.UserId;
            item.adjusted_at = Now;
            item.adjustment_reason = request.Reason.Trim();
            item.fee_schedule.total_amount += newAmount - beforeAmount;

            await FailOpenAttemptsAsync(feeItemId: feeItemId, penaltyId: null, ct);
            Audit(actor, "FEE_ITEM_ADJUSTED", "FeeScheduleItem", feeItemId, new
            {
                before = new { amount = beforeAmount, dueDate = beforeDue },
                after = new { amount = newAmount, dueDate = newDue },
                reason = request.Reason.Trim(),
            });
            await NotifyVendorAsync(item.fee_schedule.contract.vendor_id, FinanceNotificationTypes.Fee,
                "Kỳ phí thuê ô được điều chỉnh",
                $"Ô {item.fee_schedule.contract.slot.slot_code}: kỳ phí đổi từ {Vnd(beforeAmount)} (hạn {beforeDue:dd/MM/yyyy}) " +
                $"thành {Vnd(newAmount)} (hạn {newDue:dd/MM/yyyy}). Lý do: {request.Reason.Trim()}",
                "FeeScheduleItem", feeItemId, ct);
            message = $"Đã điều chỉnh kỳ phí thành {Vnd(newAmount)}, hạn {newDue:dd/MM/yyyy}.";
        }, ct);
        return new WardFinanceActionResultDto(message);
    }

    public async Task<WardFinanceActionResultDto> WaiveFeeItemAsync(
        WardActor actor, long feeItemId, string reason, CancellationToken ct)
    {
        var message = "";
        await Write(async () =>
        {
            var item = await LoadOwnedOpenItemAsync(actor, feeItemId, ct);
            var amount = item.amount;
            item.original_amount ??= amount;
            item.item_status = DebtStatuses.FeeItemCancelled;
            item.adjusted_by = actor.UserId;
            item.adjusted_at = Now;
            item.adjustment_reason = reason.Trim();
            item.fee_schedule.total_amount -= amount;

            await FailOpenAttemptsAsync(feeItemId: feeItemId, penaltyId: null, ct);
            Audit(actor, "FEE_ITEM_WAIVED", "FeeScheduleItem", feeItemId,
                new { amount, dueDate = item.due_date, reason = reason.Trim() });
            await NotifyVendorAsync(item.fee_schedule.contract.vendor_id, FinanceNotificationTypes.Fee,
                "Kỳ phí thuê ô được miễn",
                $"Ô {item.fee_schedule.contract.slot.slot_code}: kỳ phí {Vnd(amount)} hạn {item.due_date:dd/MM/yyyy} được miễn. Lý do: {reason.Trim()}",
                "FeeScheduleItem", feeItemId, ct);
            message = $"Đã miễn kỳ phí {Vnd(amount)}.";
        }, ct);
        return new WardFinanceActionResultDto(message);
    }

    public async Task<WardFinanceActionResultDto> WaivePenaltyAsync(
        WardActor actor, long penaltyId, string reason, CancellationToken ct)
    {
        var message = "";
        await Write(async () =>
        {
            var penalty = await db.Penalties
                .Include(p => p.violation)
                .Where(p => db.Violations.Where(ViolationInWard(actor.WardId)).Select(v => v.violation_id).Contains(p.violation_id))
                .SingleOrDefaultAsync(p => p.penalty_id == penaltyId, ct)
                ?? throw new NotFoundException(FinanceMessages.PenaltyNotFound);
            if (!PenaltyStatuses.IsPayable(penalty.penalty_status))
            {
                throw new DomainRuleException(FinanceMessages.PenaltyNoLongerPayable);
            }

            penalty.penalty_status = PenaltyStatuses.Waived;
            penalty.waived_by = actor.UserId;
            penalty.waiver_reason = reason.Trim();
            penalty.waived_at = Now;

            await FailOpenAttemptsAsync(feeItemId: null, penaltyId: penaltyId, ct);
            Audit(actor, "PENALTY_WAIVED", "Penalty", penaltyId,
                new { amount = penalty.amount, decisionNumber = penalty.decision_number, reason = reason.Trim() });
            if (penalty.violation.vendor_id is { } vendorId)
            {
                await NotifyVendorAsync(vendorId, FinanceNotificationTypes.Penalty,
                    "Khoản phạt được miễn",
                    $"Khoản phạt {Vnd(penalty.amount)}" +
                    (penalty.decision_number is null ? "" : $" (quyết định số {penalty.decision_number})") +
                    $" được miễn nộp. Lý do: {reason.Trim()}",
                    "Penalty", penaltyId, ct);
            }

            message = $"Đã miễn khoản phạt {Vnd(penalty.amount)}.";
        }, ct);
        return new WardFinanceActionResultDto(message);
    }

    public async Task<WardFinanceActionResultDto> RecordOfflinePaymentAsync(
        WardActor actor, RecordOfflinePaymentRequest request, CancellationToken ct)
    {
        var isFee = request.Target == "FEE";
        await finance.RecordOfflinePaymentAsync(
            actor.WardId, actor.UserId,
            isFee ? request.TargetId : null,
            isFee ? null : request.TargetId,
            request.Method, request.Reference?.Trim(), request.Note?.Trim(), ct);
        return new WardFinanceActionResultDto(
            "Đã ghi nhận khoản thu và phát hành hoá đơn cho hộ kinh doanh.");
    }

    public async Task<WardFinanceActionResultDto> SetSlotPriceAsync(
        WardActor actor, long slotId, SetSlotPriceRequest request, CancellationToken ct)
    {
        var message = "";
        await Write(async () =>
        {
            var slot = await db.SidewalkSlots
                .Include(s => s.zone)
                .SingleOrDefaultAsync(s => s.slot_id == slotId && s.zone.ward_unit_id == actor.WardId, ct)
                ?? throw new NotFoundException(SideMessages.SlotNotFound);

            if (slot.price_override == request.PriceOverride)
            {
                throw new WardException(400, "no_change", "Giá riêng mới trùng với giá hiện tại của ô.");
            }

            var before = slot.price_override;
            slot.price_override = request.PriceOverride;
            var monthly = slot.zone.rental_mode == RentalModes.Standard;
            var unit = monthly ? "tháng" : "ngày";

            Audit(actor, "SLOT_PRICE_SET", "SidewalkSlot", slotId,
                new { before, after = request.PriceOverride, reason = request.Reason.Trim() });

            // Whoever is waiting on this slot will be billed the new price when it is approved.
            var waiting = await db.RentalApplications.AsNoTracking()
                .Where(a => a.slot_id == slotId && ApplicationStatuses.Open.Contains(a.application_status))
                .Select(a => a.registration.vendor.user_id)
                .Distinct()
                .ToListAsync(ct);
            var now = Now;
            var priceText = request.PriceOverride is { } price
                ? $"{Vnd(price)}/{unit}"
                : "theo giá chung của khu vực";
            foreach (var userId in waiting)
            {
                db.Notifications.Add(new Notification
                {
                    user_id = userId,
                    notification_type = "SLOT_PRICE_CHANGED",
                    title = $"Giá thuê ô {slot.slot_code} thay đổi",
                    body = $"Ô {slot.slot_code} nay được tính {priceText}. Áp dụng khi đơn của bạn được duyệt. " +
                           $"Lý do: {request.Reason.Trim()}",
                    related_entity_type = "SidewalkSlot",
                    related_entity_id = slotId,
                    is_read = false,
                    sent_at = now,
                });
            }

            message = request.PriceOverride is { } p2
                ? $"Đã đặt giá riêng {Vnd(p2)}/{unit} cho ô {slot.slot_code}."
                : $"Ô {slot.slot_code} quay về giá chung của khu vực.";
        }, ct);
        return new WardFinanceActionResultDto(message);
    }

    public async Task<WardFinanceActionResultDto> RefundDepositAsync(
        WardActor actor, long contractId, RefundDepositRequest request, CancellationToken ct)
    {
        var message = "";
        await Write(async () =>
        {
            var contract = await db.RentalContracts
                .Include(c => c.slot).ThenInclude(s => s.zone)
                .SingleOrDefaultAsync(c => c.contract_id == contractId && c.slot.zone.ward_unit_id == actor.WardId, ct)
                ?? throw new NotFoundException(FinanceMessages.ContractNotFound);

            if (!HasEnded(contract))
            {
                throw new DomainRuleException(
                    "Hợp đồng còn hiệu lực. Chỉ hoàn cọc khi hợp đồng đã hết hạn hoặc đã chấm dứt.");
            }

            if (await db.DepositRefunds.AnyAsync(r => r.contract_id == contractId, ct))
            {
                throw new ConflictException("Đã hoàn cọc cho hợp đồng này.");
            }

            var depositPaid = (await db.FeeScheduleItems.AsNoTracking()
                .Where(i => i.fee_schedule.contract_id == contractId && i.item_status == FeeItemStatuses.Paid)
                .Select(i => new { i.deposit_amount, i.amount })
                .ToListAsync(ct)).Sum(i => Math.Min(i.deposit_amount, i.amount));
            if (depositPaid <= 0)
            {
                throw new DomainRuleException("Hợp đồng chưa thu khoản cọc nào để hoàn.");
            }

            if (request.Amount > depositPaid)
            {
                throw new WardException(400, "refund_exceeds_deposit",
                    $"Số tiền hoàn không được vượt quá tiền cọc đã thu ({Vnd(depositPaid)}).");
            }

            if (request.Amount < depositPaid && string.IsNullOrWhiteSpace(request.DeductionReason))
            {
                throw new WardException(400, "deduction_reason_required",
                    "Hoàn thấp hơn tiền cọc đã thu: nhập lý do khấu trừ (nợ phí, hư hại, vi phạm...).");
            }

            db.DepositRefunds.Add(new DepositRefund
            {
                contract_id = contractId,
                deposit_paid = depositPaid,
                amount = request.Amount,
                deduction_reason = string.IsNullOrWhiteSpace(request.DeductionReason) ? null : request.DeductionReason.Trim(),
                method = request.Method,
                reference = string.IsNullOrWhiteSpace(request.Reference) ? null : request.Reference.Trim(),
                refunded_by = actor.UserId,
                refunded_at = Now,
            });

            Audit(actor, "DEPOSIT_REFUNDED", "RentalContract", contractId, new
            {
                depositPaid,
                amount = request.Amount,
                method = request.Method,
                request.Reference,
                deductionReason = request.DeductionReason?.Trim(),
            });
            var deducted = depositPaid - request.Amount;
            await NotifyVendorAsync(contract.vendor_id, FinanceNotificationTypes.Fee,
                "Đã hoàn tiền đặt cọc",
                $"Ô {contract.slot.slot_code}: Phường đã hoàn {Vnd(request.Amount)} tiền đặt cọc" +
                (deducted > 0 ? $", khấu trừ {Vnd(deducted)} (lý do: {request.DeductionReason!.Trim()})." : "."),
                "RentalContract", contractId, ct);
            message = $"Đã ghi nhận hoàn cọc {Vnd(request.Amount)}.";
        }, ct);
        return new WardFinanceActionResultDto(message);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>An instalment of this ward's, still owed, on its contract's open schedule.</summary>
    private async Task<FeeScheduleItem> LoadOwnedOpenItemAsync(WardActor actor, long feeItemId, CancellationToken ct)
    {
        var item = await db.FeeScheduleItems
            .Include(i => i.fee_schedule).ThenInclude(s => s.contract).ThenInclude(c => c.slot).ThenInclude(s => s.zone)
            .SingleOrDefaultAsync(i => i.fee_item_id == feeItemId, ct);
        if (item is null || item.fee_schedule.contract.slot.zone.ward_unit_id != actor.WardId)
        {
            throw new NotFoundException(FinanceMessages.FeeItemNotFound);
        }

        if (!FeeItemStatuses.IsOutstanding(item.item_status) || item.fee_schedule.superseded_at is not null)
        {
            throw new DomainRuleException(
                "Kỳ phí này đã thanh toán, đã bị huỷ hoặc thuộc lịch phí cũ nên không thể thay đổi.");
        }

        return item;
    }

    /// <summary>A checkout opened for the old amount must not complete after the figure changed.</summary>
    private async Task FailOpenAttemptsAsync(long? feeItemId, long? penaltyId, CancellationToken ct)
    {
        var attempts = await db.PaymentTransactions
            .Where(t => t.transaction_status == PaymentStatuses.Pending
                && ((feeItemId != null && t.fee_item_id == feeItemId)
                    || (penaltyId != null && t.penalty_id == penaltyId)))
            .ToListAsync(ct);
        foreach (var attempt in attempts)
        {
            attempt.transaction_status = PaymentStatuses.Failed;
            attempt.callback_received_at = Now;
        }
    }

    private void Audit(WardActor actor, string action, string entityType, long entityId, object details) =>
        db.AuditLogs.Add(new AuditLog
        {
            actor_user_id = actor.UserId,
            action = action,
            entity_type = entityType,
            entity_id = entityId,
            details = JsonSerializer.Serialize(details),
            created_at = Now,
        });

    private async Task NotifyVendorAsync(
        long vendorId, string type, string title, string body, string entityType, long entityId, CancellationToken ct)
    {
        var userId = await db.Vendors.AsNoTracking()
            .Where(v => v.vendor_id == vendorId)
            .Select(v => v.user_id)
            .FirstOrDefaultAsync(ct);
        if (userId <= 0)
        {
            return;
        }

        db.Notifications.Add(new Notification
        {
            user_id = userId,
            notification_type = type,
            title = title,
            body = body,
            related_entity_type = entityType,
            related_entity_id = entityId,
            is_read = false,
            sent_at = Now,
        });
    }
}
