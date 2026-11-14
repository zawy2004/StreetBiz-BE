using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Security;
using StreetBiz.Infrastructure.Persistence.Repositories;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;
using StreetBiz.Infrastructure.Tests.Support;
using static StreetBiz.Infrastructure.Tests.Support.SqliteTestDatabase;

namespace StreetBiz.Infrastructure.Tests;

/// <summary>WARD-14: penalty takings, debt reminders and the owed-back (duplicate) payments the ward refunds by hand.</summary>
public sealed class WardCollectionRefundRepositoryTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 3, 0, 0, DateTimeKind.Utc);

    private static async Task<SqliteTestDatabase> SeedAsync()
    {
        var db = await CreateAsync();
        var schedule = new FeeSchedule { contract_id = ContractA, revision = 1, total_amount = 900000 };
        schedule.FeeScheduleItems.Add(new FeeScheduleItem { fee_item_id = 1, due_date = new DateOnly(2026, 10, 5), amount = 900000, item_status = FeeItemStatuses.Paid, paid_at = T0 });
        db.Db.FeeSchedules.Add(schedule);

        db.Db.ViolationTypes.Add(new ViolationType { violation_type_code = "OUTSIDE_SLOT", description = "Bày hàng ngoài ô", is_active = true });
        db.Db.PenaltyFeeSchedules.Add(new PenaltyFeeSchedule { penalty_schedule_id = 1, ward_unit_id = Ward, violation_type = "OUTSIDE_SLOT", penalty_amount = 500000, effective_from = new DateOnly(2026, 1, 1), created_by = Officer });
        db.Db.Violations.AddRange(
            new Violation { violation_id = 1, contract_id = ContractA, slot_id = SlotRented, vendor_id = VendorA, violation_type = "OUTSIDE_SLOT", recorded_by = Officer, source = "ON_SITE" },
            // No slot: an itinerant vendor's violation cannot be placed in a ward's zone, so it is not counted.
            new Violation { violation_id = 2, vendor_id = VendorB, violation_type = "OUTSIDE_SLOT", recorded_by = Officer, source = "ON_SITE" },
            new Violation { violation_id = 3, contract_id = ContractA, slot_id = SlotRented, vendor_id = VendorA, violation_type = "OUTSIDE_SLOT", recorded_by = Officer, source = "ON_SITE" });
        db.Db.Penalties.AddRange(
            new Penalty { penalty_id = 1, violation_id = 1, penalty_schedule_id = 1, amount = 500000, penalty_status = PenaltyStatuses.Paid, paid_at = T0.AddDays(2) },
            new Penalty { penalty_id = 2, violation_id = 2, penalty_schedule_id = 1, amount = 500000, penalty_status = PenaltyStatuses.Paid, paid_at = T0.AddDays(2) },
            new Penalty { penalty_id = 3, violation_id = 3, penalty_schedule_id = 1, amount = 700000, penalty_status = PenaltyStatuses.Paid, paid_at = T0.AddDays(40) });

        db.Db.PaymentTransactions.AddRange(
            Payment(1, PaymentPurposes.RentalFee, feeItem: 1, status: PaymentStatuses.Success, created: T0),
            Payment(2, PaymentPurposes.RentalFee, feeItem: 1, status: PaymentStatuses.RefundRequired, created: T0.AddMinutes(1), callback: T0.AddMinutes(2)),
            Payment(3, PaymentPurposes.Penalty, penalty: 1, status: PaymentStatuses.Refunded, created: T0.AddDays(2)),
            Payment(4, PaymentPurposes.Penalty, penalty: 1, status: PaymentStatuses.RefundRequired, created: T0.AddDays(3)),
            Payment(5, PaymentPurposes.Penalty, penalty: 2, status: PaymentStatuses.RefundRequired, created: T0.AddDays(3)));
        db.Db.AuditLogs.AddRange(
            new AuditLog { actor_user_id = Officer, action = "FINANCE_PAYMENT_REFUNDED", entity_type = "PaymentTransaction", entity_id = 3, created_at = T0.AddDays(4) },
            new AuditLog { actor_user_id = Officer, action = "FINANCE_PAYMENT_REFUNDED", entity_type = "PaymentTransaction", entity_id = 3, created_at = T0.AddDays(5) });
        await db.Db.SaveChangesAsync();
        db.Db.ChangeTracker.Clear();
        return db;
    }

    private static PaymentTransaction Payment(long id, string purpose, string status, DateTime created, long? feeItem = null, long? penalty = null, DateTime? callback = null) => new()
    {
        transaction_id = id, idempotency_key = $"k-{id}", payment_purpose = purpose, fee_item_id = feeItem, penalty_id = penalty,
        provider = "PAYOS", provider_reference = $"ref-{id}", amount = penalty is null ? 900000 : 500000,
        transaction_status = status, created_at = created, callback_received_at = callback,
    };

    [Fact]
    public async Task Penalty_takings_count_only_paid_penalties_on_a_ward_slot_inside_the_window()
    {
        using var db = await SeedAsync();
        var repo = new WardCollectionRepository(db.Db);

        var paid = await repo.ListPenaltyPaymentsAsync(Ward, T0, T0.AddDays(30), default);

        var row = Assert.Single(paid);
        Assert.Equal(500000, row.Amount);
        Assert.Empty(await repo.ListPenaltyPaymentsAsync(Ward + 1, T0, T0.AddDays(30), default));
    }

    [Fact]
    public async Task No_contracts_means_no_reminder_lookup_and_a_second_reminder_the_same_day_is_not_sent()
    {
        using var db = await SeedAsync();
        var repo = new WardCollectionRepository(db.Db);

        Assert.Empty(await repo.GetLastDebtRemindersAsync([], default));

        Assert.Null(await repo.SendDebtReminderAsync(ContractA, VendorA, Officer, "Phường nhắc", "Ô S-01 nợ phí", T0.AddHours(1), T0.Date, default));
        var again = await repo.SendDebtReminderAsync(ContractA, VendorA, Officer, "Phường nhắc", "Ô S-01 nợ phí", T0.AddHours(5), T0.Date, default);

        Assert.Equal(T0.AddHours(1), again);
        Assert.Equal(DateTimeKind.Utc, again!.Value.Kind);
        using var read = db.NewContext();
        Assert.Equal(1, await read.Notifications.CountAsync(n => n.related_entity_id == ContractA));
    }

    [Fact]
    public async Task Refunds_list_owed_back_first_then_newest_and_skip_payments_outside_the_ward()
    {
        using var db = await SeedAsync();
        var repo = new WardCollectionRepository(db.Db);

        var refunds = await repo.ListRefundsAsync(Ward, default);

        // Transaction 5 is a penalty on a violation with no slot, so no ward owns it.
        Assert.Equal([4L, 2L, 3L], refunds.Select(r => r.TransactionId));
        var fee = refunds.Single(r => r.TransactionId == 2);
        Assert.Equal(("FEE", VendorA, "User 10", "S-01", "phí thuê ô S-01 hạn 05/10/2026"), (fee.Kind, fee.VendorUserId, fee.VendorName, fee.SlotCode, fee.Label));
        Assert.Equal(T0.AddMinutes(2), fee.ReceivedAt); // the provider callback time wins over creation
        Assert.Null(fee.RefundedAt);
        var refunded = refunds.Single(r => r.TransactionId == 3);
        Assert.Equal(("PENALTY", "tiền phạt: Bày hàng ngoài ô"), (refunded.Kind, refunded.Label));
        Assert.Equal(T0.AddDays(5), refunded.RefundedAt); // the latest refund entry
    }

    [Fact]
    public async Task Marking_a_refund_settles_the_payment_logs_it_and_tells_the_vendor()
    {
        using var db = await SeedAsync();
        var repo = new WardCollectionRepository(db.Db);

        var row = await repo.MarkRefundedAsync(Ward, 2, Officer, "Chuyển khoản lại", T0.AddDays(6), default);

        Assert.NotNull(row);
        Assert.Equal(PaymentStatuses.Refunded, row!.Status);
        Assert.Equal(T0.AddDays(6), row.RefundedAt);
        using var read = db.NewContext();
        Assert.Equal(PaymentStatuses.Refunded, (await read.PaymentTransactions.SingleAsync(p => p.transaction_id == 2)).transaction_status);
        var audit = await read.AuditLogs.SingleAsync(l => l.entity_id == 2 && l.action == "FINANCE_PAYMENT_REFUNDED");
        Assert.Contains("Chuyển khoản lại", System.Text.RegularExpressions.Regex.Unescape(audit.details!));
        var notice = await read.Notifications.SingleAsync(n => n.related_entity_id == 2);
        Assert.Equal((VendorA, FinanceNotificationTypes.Fee), (notice.user_id, notice.notification_type));
        Assert.Contains("900.000 đ", notice.body);
    }

    [Fact]
    public async Task A_penalty_refund_notifies_as_a_penalty()
    {
        using var db = await SeedAsync();
        var repo = new WardCollectionRepository(db.Db);

        await repo.MarkRefundedAsync(Ward, 4, Officer, null, T0.AddDays(6), default);

        using var read = db.NewContext();
        Assert.Equal(FinanceNotificationTypes.Penalty, (await read.Notifications.SingleAsync(n => n.related_entity_id == 4)).notification_type);
    }

    [Fact]
    public async Task An_already_refunded_payment_is_refused_and_an_unknown_or_foreign_one_is_not_found()
    {
        using var db = await SeedAsync();
        var repo = new WardCollectionRepository(db.Db);

        await Assert.ThrowsAsync<DomainRuleException>(() => repo.MarkRefundedAsync(Ward, 3, Officer, null, T0, default));
        Assert.Null(await repo.MarkRefundedAsync(Ward, 999, Officer, null, T0, default));
        Assert.Null(await repo.MarkRefundedAsync(Ward + 1, 2, Officer, null, T0, default));
        Assert.Null(await repo.MarkRefundedAsync(Ward, 1, Officer, null, T0, default)); // a normal payment is not a refund case
    }
}
