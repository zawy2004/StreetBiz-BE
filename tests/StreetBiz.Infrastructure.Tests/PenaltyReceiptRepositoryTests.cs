using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.PenaltyReceipts;
using StreetBiz.Infrastructure.Persistence.Repositories;
using static StreetBiz.Infrastructure.Tests.PayeeTestFixture;

namespace StreetBiz.Infrastructure.Tests;

/// <summary>Fines are paid to the State Treasury: the household's receipt, the ward's check of it, and an annulled decision.</summary>
public sealed class PenaltyReceiptRepositoryTests
{
    private static readonly SubmitPenaltyReceipt Receipt =
        new("/api/uploads/evidence/7/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.jpg", "KB-2026-1", new DateOnly(2026, 11, 2), "Nộp tại Kho bạc");

    private static PenaltyReceiptRepository Repository(PayeeTestFixture f) => new(f.Db);

    [Fact]
    public async Task A_household_sees_its_own_fine_with_the_ward_and_no_receipt_yet()
    {
        using var f = await Create();

        var treasury = await Repository(f).GetTreasuryAsync(Vendor, Penalty, default);

        Assert.Equal(500_000m, treasury!.Amount);
        Assert.Equal("QĐXP-2026/0001", treasury.DecisionNumber);
        Assert.Equal("Cản trở lối đi bộ", treasury.ViolationLabel);
        Assert.Equal("S-01", treasury.SlotCode);
        Assert.Equal("Phường Hải Châu", treasury.WardName);
        Assert.Equal("UNPAID", treasury.PenaltyStatus);
        Assert.Null(treasury.LatestReceipt);
        // Not another household's fine.
        Assert.Null(await Repository(f).GetTreasuryAsync(OtherVendor, Penalty, default));
    }

    [Fact]
    public async Task A_receipt_is_kept_and_the_ward_officers_are_told()
    {
        using var f = await Create();

        var row = await Repository(f).SubmitAsync(Vendor, VendorUser, Penalty, Receipt, Now.UtcDateTime, default);

        Assert.Equal("SUBMITTED", row.Status);
        Assert.Equal(new DateOnly(2026, 11, 2), row.PaidOn);
        var latest = (await Repository(f).GetTreasuryAsync(Vendor, Penalty, default))!.LatestReceipt;
        Assert.Equal(row.ReceiptId, latest!.ReceiptId);
        var told = await f.Db.Notifications.AsNoTracking().ToListAsync();
        Assert.Equal([Officer], told.Select(n => n.user_id)); // only the ward of the fine
        Assert.Equal("PENALTY", told[0].notification_type);
    }

    [Fact]
    public async Task A_second_receipt_while_one_waits_is_a_conflict_and_a_paid_fine_takes_none()
    {
        using var f = await Create();
        await Repository(f).SubmitAsync(Vendor, VendorUser, Penalty, Receipt, Now.UtcDateTime, default);

        await Assert.ThrowsAsync<ConflictException>(() =>
            Repository(f).SubmitAsync(Vendor, VendorUser, Penalty, Receipt, Now.UtcDateTime, default));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            Repository(f).SubmitAsync(OtherVendor, OtherVendorUser, Penalty, Receipt, Now.UtcDateTime, default));

        var paid = await f.Db.Penalties.SingleAsync(p => p.penalty_id == OtherWardPenalty);
        paid.penalty_status = "PAID";
        await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            Repository(f).SubmitAsync(Vendor, VendorUser, OtherWardPenalty, Receipt, Now.UtcDateTime, default));
    }

    [Fact]
    public async Task Only_the_fines_own_ward_may_open_the_receipt_file()
    {
        using var f = await Create();
        await Repository(f).SubmitAsync(Vendor, VendorUser, Penalty, Receipt, Now.UtcDateTime, default);

        Assert.True(await Repository(f).ReceiptBelongsToWardAsync(Receipt.ReceiptUrl, Ward, default));
        Assert.False(await Repository(f).ReceiptBelongsToWardAsync(Receipt.ReceiptUrl, OtherWard, default));
        Assert.False(await Repository(f).ReceiptBelongsToWardAsync("/api/uploads/evidence/7/other.jpg", Ward, default));
    }

    [Fact]
    public async Task The_ward_lists_only_its_own_receipts_waiting_first()
    {
        using var f = await Create();
        var first = await Repository(f).SubmitAsync(Vendor, VendorUser, Penalty, Receipt, Now.UtcDateTime, default);
        await Repository(f).SubmitAsync(Vendor, VendorUser, OtherWardPenalty, Receipt, Now.UtcDateTime, default);
        await Repository(f).DecideAsync(Ward, Officer, first.ReceiptId, false, "Ảnh mờ", Now.UtcDateTime, default);
        var resubmitted = await Repository(f).SubmitAsync(Vendor, VendorUser, Penalty, Receipt, Now.UtcDateTime.AddHours(1), default);

        var mine = await Repository(f).ListForWardAsync(Ward, null, default);
        var otherWard = await Repository(f).ListForWardAsync(OtherWard, null, default);
        var waiting = await Repository(f).ListForWardAsync(Ward, "SUBMITTED", default);

        Assert.Equal([resubmitted.ReceiptId, first.ReceiptId], mine.Select(r => r.Receipt.ReceiptId));
        Assert.Equal("Cản trở lối đi bộ", mine[0].ViolationLabel);
        Assert.Equal("User 7", mine[0].VendorName);
        Assert.Equal("Ảnh mờ", mine[1].Receipt.ReviewNote);
        Assert.Equal("User 1", mine[1].ReviewedByName);
        Assert.Single(otherWard);
        Assert.Single(waiting);
    }

    [Fact]
    public async Task A_decision_is_final_is_audited_and_tells_the_household()
    {
        using var f = await Create();
        var row = await Repository(f).SubmitAsync(Vendor, VendorUser, Penalty, Receipt, Now.UtcDateTime, default);
        f.Db.Notifications.RemoveRange(f.Db.Notifications);
        await f.Db.SaveChangesAsync();

        var review = await Repository(f).GetForReviewAsync(Ward, row.ReceiptId, default);
        Assert.Equal("KB-2026-1", review!.Reference);
        Assert.Equal("UNPAID", review.PenaltyStatus);
        Assert.Equal(VendorUser, review.VendorUserId);
        Assert.Null(await Repository(f).GetForReviewAsync(OtherWard, row.ReceiptId, default)); // another ward: not found

        var decided = await Repository(f).DecideAsync(Ward, Officer, row.ReceiptId, true, "Đúng biên lai", Now.UtcDateTime, default);

        Assert.Equal("ACCEPTED", decided.Status);
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            Repository(f).DecideAsync(Ward, Officer, row.ReceiptId, false, "x", Now.UtcDateTime, default));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            Repository(f).DecideAsync(OtherWard, OtherWardOfficer, row.ReceiptId, true, null, Now.UtcDateTime, default));
        Assert.Equal("PENALTY_RECEIPT_ACCEPTED", (await f.Db.AuditLogs.AsNoTracking().SingleAsync()).action);
        var notice = await f.Db.Notifications.AsNoTracking().SingleAsync();
        Assert.Equal(VendorUser, notice.user_id);
        Assert.Contains("đã ghi nhận", notice.body);
    }

    [Fact]
    public async Task An_annulled_decision_cancels_the_fine_and_marks_its_payment_as_owed_back_by_the_Treasury()
    {
        using var f = await Create();
        var penalty = await f.Db.Penalties.SingleAsync(p => p.penalty_id == Penalty);
        penalty.penalty_status = "PAID";
        penalty.paid_at = Now.UtcDateTime;
        f.Db.PaymentTransactions.Add(new StreetBiz.Infrastructure.Persistence.ScaffoldedModels.PaymentTransaction
        {
            idempotency_key = "OFFLINE-1", payment_purpose = "PENALTY", penalty_id = Penalty, provider = "TREASURY",
            provider_reference = "KB-2026-1", amount = 500_000m, transaction_status = "SUCCESS", created_at = Now.UtcDateTime,
        });
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();

        var message = await Repository(f).AnnulPaidPenaltyAsync(Ward, Officer, Penalty, "Quyết định bị huỷ sau khiếu nại", Now.UtcDateTime, default);

        Assert.Contains("Kho bạc", message);
        var after = await f.Db.Penalties.AsNoTracking().SingleAsync(p => p.penalty_id == Penalty);
        Assert.Equal("CANCELLED", after.penalty_status);
        Assert.Equal(Officer, after.waived_by);
        Assert.Equal("Quyết định bị huỷ sau khiếu nại", after.waiver_reason);
        Assert.Equal("REFUND_REQUIRED", (await f.Db.PaymentTransactions.AsNoTracking().SingleAsync()).transaction_status);
        Assert.Equal("PENALTY_ANNULLED_AFTER_PAYMENT", (await f.Db.AuditLogs.AsNoTracking().SingleAsync()).action);
        Assert.Contains("Kho bạc", (await f.Db.Notifications.AsNoTracking().SingleAsync()).body);
    }

    [Fact]
    public async Task Only_a_paid_fine_of_the_ward_can_be_annulled_this_way()
    {
        using var f = await Create();

        // Unpaid: that is a waiver, which has its own flow.
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            Repository(f).AnnulPaidPenaltyAsync(Ward, Officer, Penalty, "x", Now.UtcDateTime, default));
        // Another ward's fine is not found.
        await Assert.ThrowsAsync<NotFoundException>(() =>
            Repository(f).AnnulPaidPenaltyAsync(Ward, Officer, OtherWardPenalty, "x", Now.UtcDateTime, default));
    }
}
