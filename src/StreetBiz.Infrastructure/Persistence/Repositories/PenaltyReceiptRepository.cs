using System.Data;
using System.Globalization;
using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.PenaltyReceipts;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Persistence.Repositories;

/// <summary>
/// Fines are paid to the State Treasury, not through StreetBiz. This keeps the household's receipt
/// and the ward's decision on it, and records a fine whose decision was annulled after it was paid.
/// </summary>
public sealed class PenaltyReceiptRepository(StreetBizDbContext db) : IPenaltyReceiptRepository
{
    private const string WardAuthority = "WARD_AUTHORITY";
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");

    /// <summary>A violation has no ward column: its slot's ward, else its contract's slot's, else the recording officer's.</summary>
    private static Expression<Func<Violation, bool>> ViolationInWard(int wardId) => v =>
        (v.slot_id != null && v.slot!.zone.ward_unit_id == wardId)
        || (v.slot_id == null && v.contract_id != null && v.contract!.slot.zone.ward_unit_id == wardId)
        || (v.slot_id == null && v.contract_id == null && v.UserAccount!.ward_unit_id == wardId);

    private IQueryable<Penalty> PenaltiesWithViolation() => db.Penalties
        .Include(p => p.violation).ThenInclude(v => v.slot).ThenInclude(s => s!.zone)
        .Include(p => p.violation).ThenInclude(v => v.contract).ThenInclude(c => c!.slot).ThenInclude(s => s.zone)
        .Include(p => p.violation).ThenInclude(v => v.UserAccount)
        .Include(p => p.violation).ThenInclude(v => v.violation_typeNavigation);

    private static int? WardOf(Penalty penalty) =>
        penalty.violation.slot?.zone.ward_unit_id
        ?? penalty.violation.contract?.slot.zone.ward_unit_id
        ?? penalty.violation.UserAccount?.ward_unit_id;

    // ---------------------------------------------------------------------------- household

    public async Task<PenaltyTreasuryRow?> GetTreasuryAsync(long vendorId, long penaltyId, CancellationToken cancellationToken)
    {
        var penalty = await PenaltiesWithViolation().AsNoTracking()
            .SingleOrDefaultAsync(p => p.penalty_id == penaltyId && p.violation.vendor_id == vendorId, cancellationToken);
        if (penalty is null)
        {
            return null;
        }

        var wardId = WardOf(penalty);
        var wardName = wardId is { } id
            ? await db.AdministrativeUnits.AsNoTracking().Where(u => u.unit_id == id).Select(u => u.unit_name).SingleOrDefaultAsync(cancellationToken)
            : null;
        var latest = await db.PenaltyPaymentReceipts.AsNoTracking()
            .Where(r => r.penalty_id == penaltyId)
            .OrderByDescending(r => r.receipt_id)
            .FirstOrDefaultAsync(cancellationToken);
        return new PenaltyTreasuryRow(
            penalty.penalty_id,
            penalty.amount,
            penalty.decision_number,
            penalty.violation.violation_typeNavigation.description,
            penalty.violation.slot?.slot_code,
            wardName,
            penalty.penalty_status,
            penalty.created_at,
            latest is null ? null : ToRow(latest));
    }

    public async Task<PenaltyReceiptRow> SubmitAsync(
        long vendorId, long userId, long penaltyId, SubmitPenaltyReceipt receipt, DateTime now, CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            var penalty = await PenaltiesWithViolation()
                .SingleOrDefaultAsync(p => p.penalty_id == penaltyId && p.violation.vendor_id == vendorId, cancellationToken)
                ?? throw new NotFoundException(PenaltyReceiptMessages.PenaltyNotFound);
            if (!PenaltyStatuses.IsPayable(penalty.penalty_status))
            {
                throw new DomainRuleException(PenaltyReceiptMessages.NotPayable);
            }

            if (await db.PenaltyPaymentReceipts.AnyAsync(
                r => r.penalty_id == penaltyId && r.status == PenaltyReceiptStatuses.Submitted, cancellationToken))
            {
                throw new ConflictException(PenaltyReceiptMessages.AlreadyWaiting);
            }

            var row = new PenaltyPaymentReceipt
            {
                penalty_id = penaltyId,
                submitted_by = userId,
                receipt_url = receipt.ReceiptUrl,
                reference = receipt.Reference,
                paid_on = receipt.PaidOn,
                note = receipt.Note,
                status = PenaltyReceiptStatuses.Submitted,
                submitted_at = now,
            };
            db.PenaltyPaymentReceipts.Add(row);

            var vendorName = await db.Vendors.AsNoTracking()
                .Where(v => v.vendor_id == vendorId).Select(v => v.user.full_name).SingleOrDefaultAsync(cancellationToken);
            if (WardOf(penalty) is { } wardId)
            {
                var officers = await db.UserAccounts.AsNoTracking()
                    .Where(u => u.ward_unit_id == wardId && u.role_code == WardAuthority && u.account_status == "ACTIVE")
                    .Select(u => u.user_id)
                    .ToListAsync(cancellationToken);
                foreach (var officer in officers)
                {
                    Notify(officer, "Có biên lai nộp phạt chờ kiểm tra",
                        $"{vendorName ?? "Hộ kinh doanh"} gửi biên lai nộp Kho bạc khoản phạt {Vnd(penalty.amount)}" +
                        (penalty.decision_number is null ? "" : $" (quyết định số {penalty.decision_number})") +
                        ". Mở mục Thu phí → Biên lai nộp phạt để kiểm tra.",
                        penaltyId, now);
                }
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ToRow(row);
        });
    }

    // --------------------------------------------------------------------------------- ward

    public async Task<IReadOnlyList<WardPenaltyReceiptRow>> ListForWardAsync(
        int wardId, string? status, CancellationToken cancellationToken)
    {
        var inWard = db.Violations.Where(ViolationInWard(wardId)).Select(v => v.violation_id);
        var query = db.PenaltyPaymentReceipts.AsNoTracking().Where(r => inWard.Contains(r.penalty.violation_id));
        if (status is not null)
        {
            query = query.Where(r => r.status == status);
        }

        var receipts = await query
            .OrderBy(r => r.status == PenaltyReceiptStatuses.Submitted ? 0 : 1)
            .ThenByDescending(r => r.receipt_id)
            .Take(200)
            .ToListAsync(cancellationToken);
        if (receipts.Count == 0)
        {
            return [];
        }

        var penaltyIds = receipts.Select(r => r.penalty_id).Distinct().ToList();
        var penalties = (await PenaltiesWithViolation().AsNoTracking()
                .Where(p => penaltyIds.Contains(p.penalty_id))
                .ToListAsync(cancellationToken))
            .ToDictionary(p => p.penalty_id);
        var vendorIds = penalties.Values.Select(p => p.violation.vendor_id).OfType<long>().Distinct().ToList();
        var vendorNames = await db.Vendors.AsNoTracking()
            .Where(v => vendorIds.Contains(v.vendor_id))
            .Select(v => new { v.vendor_id, v.user.full_name })
            .ToDictionaryAsync(v => v.vendor_id, v => v.full_name, cancellationToken);
        var reviewerIds = receipts.Where(r => r.reviewed_by != null).Select(r => r.reviewed_by!.Value).Distinct().ToList();
        var reviewerNames = await db.UserAccounts.AsNoTracking()
            .Where(u => reviewerIds.Contains(u.user_id))
            .Select(u => new { u.user_id, u.full_name })
            .ToDictionaryAsync(u => u.user_id, u => u.full_name, cancellationToken);

        return [.. receipts.Select(receipt =>
        {
            var penalty = penalties[receipt.penalty_id];
            return new WardPenaltyReceiptRow(
                ToRow(receipt),
                penalty.violation.vendor_id is { } vendorId ? vendorNames.GetValueOrDefault(vendorId) : null,
                penalty.amount,
                penalty.decision_number,
                penalty.violation.violation_typeNavigation.description,
                penalty.violation.slot?.slot_code,
                penalty.penalty_status,
                receipt.reviewed_by is { } reviewer ? reviewerNames.GetValueOrDefault(reviewer) : null);
        })];
    }

    public async Task<PenaltyReceiptReview?> GetForReviewAsync(int wardId, long receiptId, CancellationToken cancellationToken)
    {
        var receipt = await db.PenaltyPaymentReceipts.AsNoTracking()
            .SingleOrDefaultAsync(r => r.receipt_id == receiptId, cancellationToken);
        if (receipt is null)
        {
            return null;
        }

        var penalty = await PenaltiesWithViolation().AsNoTracking()
            .SingleOrDefaultAsync(p => p.penalty_id == receipt.penalty_id, cancellationToken);
        if (penalty is null || WardOf(penalty) != wardId)
        {
            return null; // another ward's receipt is not found, not forbidden
        }

        var vendorUserId = penalty.violation.vendor_id is { } vendorId
            ? await db.Vendors.AsNoTracking().Where(v => v.vendor_id == vendorId).Select(v => (long?)v.user_id).SingleOrDefaultAsync(cancellationToken)
            : null;
        return new PenaltyReceiptReview(
            receipt.receipt_id, receipt.penalty_id, receipt.status, receipt.reference, penalty.penalty_status, penalty.amount, vendorUserId);
    }

    public async Task<bool> ReceiptBelongsToWardAsync(string receiptUrl, int wardId, CancellationToken cancellationToken)
    {
        var inWard = db.Violations.Where(ViolationInWard(wardId)).Select(v => v.violation_id);
        return await db.PenaltyPaymentReceipts.AsNoTracking()
            .AnyAsync(r => r.receipt_url == receiptUrl && inWard.Contains(r.penalty.violation_id), cancellationToken);
    }

    public async Task<PenaltyReceiptRow> DecideAsync(
        int wardId, long officerUserId, long receiptId, bool accepted, string? note, DateTime now, CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            var receipt = await db.PenaltyPaymentReceipts.SingleOrDefaultAsync(r => r.receipt_id == receiptId, cancellationToken)
                ?? throw new NotFoundException(PenaltyReceiptMessages.ReceiptNotFound);
            var penalty = await PenaltiesWithViolation().SingleAsync(p => p.penalty_id == receipt.penalty_id, cancellationToken);
            if (WardOf(penalty) != wardId)
            {
                throw new NotFoundException(PenaltyReceiptMessages.ReceiptNotFound);
            }

            if (receipt.status != PenaltyReceiptStatuses.Submitted)
            {
                throw new DomainRuleException(PenaltyReceiptMessages.ReceiptNotWaiting);
            }

            receipt.status = accepted ? PenaltyReceiptStatuses.Accepted : PenaltyReceiptStatuses.Rejected;
            receipt.reviewed_by = officerUserId;
            receipt.reviewed_at = now;
            receipt.review_note = string.IsNullOrWhiteSpace(note) ? null : note;
            db.AuditLogs.Add(new AuditLog
            {
                actor_user_id = officerUserId,
                action = accepted ? "PENALTY_RECEIPT_ACCEPTED" : "PENALTY_RECEIPT_REJECTED",
                entity_type = "Penalty",
                entity_id = penalty.penalty_id,
                details = JsonSerializer.Serialize(new { receiptId, reference = receipt.reference, note }),
                created_at = now,
            });

            if (penalty.violation.vendor_id is { } vendorId)
            {
                var userId = await db.Vendors.AsNoTracking().Where(v => v.vendor_id == vendorId).Select(v => (long?)v.user_id).SingleOrDefaultAsync(cancellationToken);
                if (userId is { } vendorUser)
                {
                    Notify(
                        vendorUser,
                        accepted ? "Biên lai nộp phạt đã được ghi nhận" : "Biên lai nộp phạt chưa được chấp nhận",
                        accepted
                            ? $"Phường đã ghi nhận khoản phạt {Vnd(penalty.amount)} đã nộp Kho bạc. Hoá đơn nằm trong mục Hoá đơn."
                            : $"Phường chưa chấp nhận biên lai nộp phạt {Vnd(penalty.amount)}. Lý do: {note}. Bạn có thể gửi lại biên lai khác.",
                        penalty.penalty_id,
                        now);
                }
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ToRow(receipt);
        });
    }

    public async Task<string> AnnulPaidPenaltyAsync(
        int wardId, long officerUserId, long penaltyId, string reason, DateTime now, CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            var penalty = await PenaltiesWithViolation().SingleOrDefaultAsync(p => p.penalty_id == penaltyId, cancellationToken);
            if (penalty is null || WardOf(penalty) != wardId)
            {
                throw new NotFoundException(PenaltyReceiptMessages.PenaltyNotFound);
            }

            if (penalty.penalty_status != PenaltyStatuses.Paid)
            {
                throw new DomainRuleException(PenaltyReceiptMessages.NotPaid);
            }

            var payment = await db.PaymentTransactions
                .Where(t => t.penalty_id == penaltyId && t.transaction_status == PaymentStatuses.Success)
                .OrderByDescending(t => t.transaction_id)
                .FirstOrDefaultAsync(cancellationToken);
            penalty.penalty_status = PenaltyStatuses.Cancelled;
            penalty.waived_by = officerUserId;
            penalty.waiver_reason = reason;
            penalty.waived_at = now;
            if (payment is not null)
            {
                payment.transaction_status = PaymentStatuses.RefundRequired;
            }

            db.AuditLogs.Add(new AuditLog
            {
                actor_user_id = officerUserId,
                action = "PENALTY_ANNULLED_AFTER_PAYMENT",
                entity_type = "Penalty",
                entity_id = penaltyId,
                details = JsonSerializer.Serialize(new { amount = penalty.amount, decisionNumber = penalty.decision_number, reason }),
                created_at = now,
            });

            if (penalty.violation.vendor_id is { } vendorId)
            {
                var userId = await db.Vendors.AsNoTracking().Where(v => v.vendor_id == vendorId).Select(v => (long?)v.user_id).SingleOrDefaultAsync(cancellationToken);
                if (userId is { } vendorUser)
                {
                    Notify(
                        vendorUser,
                        "Quyết định xử phạt đã bị huỷ",
                        $"Quyết định xử phạt {Vnd(penalty.amount)}" +
                        (penalty.decision_number is null ? "" : $" (số {penalty.decision_number})") +
                        $" đã bị huỷ. Lý do: {reason}. Số tiền đã nộp sẽ do Kho bạc Nhà nước hoàn trả; phường sẽ ghi nhận khi Kho bạc đã hoàn.",
                        penaltyId,
                        now);
                }
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return $"Đã ghi nhận huỷ quyết định xử phạt {Vnd(penalty.amount)}. Số tiền đã nộp do Kho bạc hoàn trả; " +
                "khi Kho bạc đã hoàn, ghi nhận ở mục Báo cáo thu → Cần hoàn tiền.";
        });
    }

    // ------------------------------------------------------------------------------- helpers

    private void Notify(long userId, string title, string body, long penaltyId, DateTime now) =>
        db.Notifications.Add(new Notification
        {
            user_id = userId,
            notification_type = FinanceNotificationTypes.Penalty,
            title = title,
            body = body,
            related_entity_type = "Penalty",
            related_entity_id = penaltyId,
            is_read = false,
            sent_at = now,
        });

    private static string Vnd(decimal amount) => $"{amount.ToString("N0", Vi)} đ";

    private static PenaltyReceiptRow ToRow(PenaltyPaymentReceipt row) => new(
        row.receipt_id,
        row.penalty_id,
        row.status,
        row.receipt_url,
        row.reference,
        row.paid_on,
        row.note,
        row.submitted_at,
        row.reviewed_at,
        row.review_note);
}
