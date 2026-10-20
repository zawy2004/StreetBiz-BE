using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.WardFinance;
using StreetBiz.Application.Features.WardSlots;
using StreetBiz.Infrastructure.Payments;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Persistence.Repositories;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;
using StreetBiz.Infrastructure.Services;

namespace StreetBiz.Infrastructure.Tests;

/// <summary>
/// Ward 1 owns contract 1 (ACTIVE: a paid first instalment holding a 500 000 deposit, an OVERDUE
/// one, a PENDING one) and contract 3 (EXPIRED, deposit paid). Ward 2 owns contract 2. "Now" is
/// 10:00 on 26/09/2026 in Đà Nẵng.
/// </summary>
public sealed class WardFinanceServiceTests
{
    private const long Vendor = 10;
    private const long PaidItem = 1;
    private const long OverdueItem = 2;
    private const long PendingItem = 3;
    private const long OtherWardItem = 4;
    private const long ExpiredPaidItem = 5;
    private static readonly DateTimeOffset Morning = new(2026, 9, 26, 3, 0, 0, TimeSpan.Zero);
    private static readonly WardActor Officer = new(1, 1, "Cán bộ phường", "Chủ tịch UBND Phường");

    [Fact]
    public async Task Adjusting_an_instalment_changes_the_schedule_total_and_keeps_what_it_was()
    {
        using var f = await Fixture.Create();

        await f.Service().AdjustFeeItemAsync(
            Officer, PendingItem, new AdjustFeeItemRequest(700_000m, new DateOnly(2026, 11, 15), "Giảm theo quyết định 12"), default);

        var item = await f.Db.FeeScheduleItems.AsNoTracking().SingleAsync(i => i.fee_item_id == PendingItem);
        Assert.Equal(700_000m, item.amount);
        Assert.Equal(new DateOnly(2026, 11, 15), item.due_date);
        Assert.Equal(900_000m, item.original_amount);
        Assert.Equal(1, item.adjusted_by);
        Assert.Equal("Giảm theo quyết định 12", item.adjustment_reason);
        // 1 550 000 + 900 000 + 900 000 = 3 350 000, and this instalment drops by 200 000.
        Assert.Equal(3_150_000m, (await f.Db.FeeSchedules.AsNoTracking().SingleAsync(s => s.fee_schedule_id == 1)).total_amount);
        Assert.Single(await f.Db.AuditLogs.Where(a => a.action == "FEE_ITEM_ADJUSTED").ToListAsync());
        Assert.Single(await f.Db.Notifications.Where(n => n.user_id == Vendor).ToListAsync());
    }

    [Fact]
    public async Task Moving_an_overdue_instalment_into_the_future_makes_it_payable_again_not_overdue()
    {
        using var f = await Fixture.Create();

        await f.Service().AdjustFeeItemAsync(
            Officer, OverdueItem, new AdjustFeeItemRequest(null, new DateOnly(2026, 10, 30), "Gia hạn nộp"), default);

        var item = await f.Db.FeeScheduleItems.AsNoTracking().SingleAsync(i => i.fee_item_id == OverdueItem);
        Assert.Equal(FeeItemStatuses.Pending, item.item_status);
        Assert.Equal(900_000m, item.amount);
    }

    [Fact]
    public async Task A_checkout_opened_for_the_old_amount_is_failed_when_the_amount_changes()
    {
        using var f = await Fixture.Create();
        f.Db.PaymentTransactions.Add(new PaymentTransaction
        {
            idempotency_key = "open-1", payment_purpose = PaymentPurposes.RentalFee, fee_item_id = PendingItem,
            provider = "MOMO", amount = 900_000m, transaction_status = PaymentStatuses.Pending,
            created_at = Morning.UtcDateTime,
        });
        await f.Db.SaveChangesAsync();

        await f.Service().AdjustFeeItemAsync(
            Officer, PendingItem, new AdjustFeeItemRequest(800_000m, null, "Sửa số liệu"), default);

        Assert.Equal(PaymentStatuses.Failed,
            (await f.Db.PaymentTransactions.AsNoTracking().SingleAsync(t => t.idempotency_key == "open-1")).transaction_status);
    }

    [Fact]
    public async Task Waiving_an_instalment_cancels_it_and_takes_it_out_of_the_total()
    {
        using var f = await Fixture.Create();

        await f.Service().WaiveFeeItemAsync(Officer, OverdueItem, "Hộ khó khăn theo xác nhận của UBND", default);

        var item = await f.Db.FeeScheduleItems.AsNoTracking().SingleAsync(i => i.fee_item_id == OverdueItem);
        Assert.Equal("CANCELLED", item.item_status);
        Assert.Equal(900_000m, item.original_amount);
        Assert.Equal(2_450_000m, (await f.Db.FeeSchedules.AsNoTracking().SingleAsync(s => s.fee_schedule_id == 1)).total_amount);
    }

    [Fact]
    public async Task A_paid_instalment_and_another_wards_instalment_cannot_be_changed()
    {
        using var f = await Fixture.Create();
        var service = f.Service();

        await Assert.ThrowsAsync<DomainRuleException>(() => service.WaiveFeeItemAsync(Officer, PaidItem, "x", default));
        await Assert.ThrowsAsync<NotFoundException>(() => service.WaiveFeeItemAsync(Officer, OtherWardItem, "x", default));
        await Assert.ThrowsAsync<WardException>(() =>
            service.AdjustFeeItemAsync(Officer, PendingItem, new AdjustFeeItemRequest(900_000m, null, "Không đổi"), default));
    }

    [Fact]
    public async Task Recording_a_cash_payment_marks_it_paid_and_issues_the_invoice()
    {
        using var f = await Fixture.Create();
        var service = f.Service();

        await service.RecordOfflinePaymentAsync(
            Officer, new RecordOfflinePaymentRequest("FEE", OverdueItem, "CASH", "BL-0042", "Nộp tại bộ phận một cửa"), default);

        var item = await f.Db.FeeScheduleItems.AsNoTracking().SingleAsync(i => i.fee_item_id == OverdueItem);
        Assert.Equal(FeeItemStatuses.Paid, item.item_status);
        var invoice = await f.Db.Invoices.AsNoTracking().SingleAsync(i => i.fee_item_id == OverdueItem);
        Assert.Equal(900_000m, invoice.amount);
        var payment = await f.Db.PaymentTransactions.AsNoTracking().SingleAsync(t => t.fee_item_id == OverdueItem);
        Assert.Equal("CASH", payment.provider);
        Assert.Equal("BL-0042", payment.provider_reference);
        Assert.Equal(PaymentStatuses.Success, payment.transaction_status);

        // The same instalment cannot be recorded twice (no second invoice).
        await Assert.ThrowsAsync<DomainRuleException>(() => service.RecordOfflinePaymentAsync(
            Officer, new RecordOfflinePaymentRequest("FEE", OverdueItem, "CASH", "BL-0043", null), default));
        Assert.Single(await f.Db.Invoices.Where(i => i.fee_item_id == OverdueItem).ToListAsync());
    }

    [Fact]
    public async Task Recording_a_payment_for_another_wards_instalment_is_not_found()
    {
        using var f = await Fixture.Create();

        await Assert.ThrowsAsync<NotFoundException>(() => f.Service().RecordOfflinePaymentAsync(
            Officer, new RecordOfflinePaymentRequest("FEE", OtherWardItem, "BANK_TRANSFER", "FT123", null), default));
    }

    [Fact]
    public async Task A_slots_own_price_can_be_set_and_cleared_with_a_reason()
    {
        using var f = await Fixture.Create();
        var service = f.Service();

        await service.SetSlotPriceAsync(Officer, 1, new SetSlotPriceRequest(1_200_000m, "Vị trí đầu ngã tư"), default);
        Assert.Equal(1_200_000m, (await f.Db.SidewalkSlots.AsNoTracking().SingleAsync(s => s.slot_id == 1)).price_override);

        await service.SetSlotPriceAsync(Officer, 1, new SetSlotPriceRequest(null, "Quay về giá khu"), default);
        Assert.Null((await f.Db.SidewalkSlots.AsNoTracking().SingleAsync(s => s.slot_id == 1)).price_override);
        Assert.Equal(2, await f.Db.AuditLogs.CountAsync(a => a.action == "SLOT_PRICE_SET"));

        await Assert.ThrowsAsync<NotFoundException>(() =>
            service.SetSlotPriceAsync(Officer, 2, new SetSlotPriceRequest(1m, "Ô của phường khác"), default));
    }

    [Fact]
    public async Task A_deposit_is_refunded_only_after_the_contract_ended_and_only_once()
    {
        using var f = await Fixture.Create();
        var service = f.Service();

        // Contract 1 is still in force.
        await Assert.ThrowsAsync<DomainRuleException>(() => service.RefundDepositAsync(
            Officer, 1, new RefundDepositRequest(500_000m, "CASH", null, null), default));

        // Contract 3 ended: a partial refund must say why.
        await Assert.ThrowsAsync<WardException>(() => service.RefundDepositAsync(
            Officer, 3, new RefundDepositRequest(300_000m, "CASH", null, null), default));
        await service.RefundDepositAsync(
            Officer, 3, new RefundDepositRequest(300_000m, "BANK_TRANSFER", "FT999", "Khấu trừ hư hại ô"), default);

        var refund = await f.Db.DepositRefunds.AsNoTracking().SingleAsync(r => r.contract_id == 3);
        Assert.Equal(500_000m, refund.deposit_paid);
        Assert.Equal(300_000m, refund.amount);
        await Assert.ThrowsAsync<ConflictException>(() => service.RefundDepositAsync(
            Officer, 3, new RefundDepositRequest(500_000m, "CASH", null, null), default));
    }

    [Fact]
    public async Task A_refund_cannot_exceed_the_deposit_that_was_paid()
    {
        using var f = await Fixture.Create();

        var error = await Assert.ThrowsAsync<WardException>(() => f.Service().RefundDepositAsync(
            Officer, 3, new RefundDepositRequest(600_000m, "CASH", null, null), default));
        Assert.Equal("refund_exceeds_deposit", error.Code);
    }

    [Fact]
    public async Task The_contract_detail_reports_the_deposit_and_whether_it_can_be_refunded()
    {
        using var f = await Fixture.Create();
        var service = f.Service();

        var active = await service.GetContractAsync(Officer, 1, default);
        Assert.Equal(500_000m, active.DepositPaid);
        Assert.False(active.CanRefundDeposit);
        Assert.Equal(3, active.Items.Count);
        Assert.Equal(1_800_000m, active.Outstanding);

        var ended = await service.GetContractAsync(Officer, 3, default);
        Assert.True(ended.CanRefundDeposit);

        await Assert.ThrowsAsync<NotFoundException>(() => service.GetContractAsync(Officer, 2, default));
    }

    [Fact]
    public async Task An_unpaid_penalty_can_be_waived_with_a_reason_but_not_twice()
    {
        using var f = await Fixture.Create();
        var service = f.Service();

        await service.WaivePenaltyAsync(Officer, 1, "Khiếu nại đúng, biên bản lập nhầm", default);

        var penalty = await f.Db.Penalties.AsNoTracking().SingleAsync(p => p.penalty_id == 1);
        Assert.Equal(PenaltyStatuses.Waived, penalty.penalty_status);
        Assert.Equal(1, penalty.waived_by);
        Assert.Equal("Khiếu nại đúng, biên bản lập nhầm", penalty.waiver_reason);
        await Assert.ThrowsAsync<DomainRuleException>(() => service.WaivePenaltyAsync(Officer, 1, "Lần hai", default));
    }

    private sealed class Fixture : IDisposable
    {
        private SqliteConnection Connection { get; } = new("Data Source=:memory:");
        private TimeProvider Clock { get; init; } = null!;
        public TestContext Db { get; private set; } = null!;

        public WardFinanceService Service() =>
            new(new TestContext(Options()), new FinanceRepository(new TestContext(Options()), Clock, Microsoft.Extensions.Options.Options.Create(new PaymentGatewaySettings())), Clock);

        private DbContextOptions<StreetBizDbContext> Options() =>
            new DbContextOptionsBuilder<StreetBizDbContext>().UseSqlite(Connection).Options;

        public static async Task<Fixture> Create()
        {
            var f = new Fixture { Clock = new FixedClock(Morning) };
            await f.Connection.OpenAsync();
            f.Db = new TestContext(f.Options());
            await f.Db.Database.EnsureCreatedAsync();

            f.Db.Roles.AddRange(
                new Role { role_code = "WARD_AUTHORITY", role_name = "Ward" },
                new Role { role_code = "VENDOR", role_name = "Vendor" });
            f.Db.AdministrativeUnits.AddRange(
                new AdministrativeUnit { unit_id = 1, unit_type = "WARD", unit_name = "One" },
                new AdministrativeUnit { unit_id = 2, unit_type = "WARD", unit_name = "Two" });
            f.Db.UserAccounts.AddRange(
                new UserAccount { user_id = 1, phone_number = "0900000001", password_hash = "x", full_name = "Cán bộ", role_code = "WARD_AUTHORITY", ward_unit_id = 1, account_status = "ACTIVE" },
                new UserAccount { user_id = 2, phone_number = "0900000002", password_hash = "x", full_name = "Cán bộ 2", role_code = "WARD_AUTHORITY", ward_unit_id = 2, account_status = "ACTIVE" },
                new UserAccount { user_id = Vendor, phone_number = "0900000010", password_hash = "x", full_name = "Chủ hộ", role_code = "VENDOR", account_status = "ACTIVE" });
            f.Db.Vendors.Add(new Vendor { vendor_id = Vendor, user_id = Vendor });
            f.Db.BusinessRegistrations.Add(new BusinessRegistration
            {
                registration_id = 1, vendor_id = Vendor, ward_unit_id = 1, vendor_type = "ITINERANT",
                display_name = "Hộ A", registration_status = "APPROVED",
            });
            f.Db.PricingZones.AddRange(
                new PricingZone { zone_id = 1, ward_unit_id = 1, ward_unit_type = "WARD", zone_name = "Zone 1", price_per_day = 30_000, created_by = 1 },
                new PricingZone { zone_id = 2, ward_unit_id = 2, ward_unit_type = "WARD", zone_name = "Zone 2", price_per_day = 30_000, created_by = 2 });
            f.Db.SidewalkSlots.AddRange(
                Slot(1, 1), Slot(2, 2), Slot(3, 1));
            foreach (var (id, slot, status, end) in new[]
                     {
                         (1L, 1L, "ACTIVE", new DateOnly(2026, 12, 29)),
                         (2L, 2L, "ACTIVE", new DateOnly(2026, 12, 29)),
                         (3L, 3L, "EXPIRED", new DateOnly(2026, 8, 30)),
                     })
            {
                f.Db.RentalApplications.Add(new RentalApplication
                {
                    application_id = id, registration_id = 1, slot_id = slot, application_method = "MANUAL_SELECTED",
                    requested_term_days = 90, application_status = "APPROVED",
                });
                f.Db.RentalContracts.Add(new RentalContract
                {
                    contract_id = id, application_id = id, slot_id = slot, vendor_id = Vendor,
                    start_date = end.AddDays(-89), end_date = end, contract_status = status,
                });
            }

            var generated = new DateTime(2026, 9, 1, 3, 0, 0, DateTimeKind.Utc);
            f.Db.FeeSchedules.AddRange(
                new FeeSchedule { fee_schedule_id = 1, contract_id = 1, revision = 1, total_amount = 3_350_000m, generated_at = generated },
                new FeeSchedule { fee_schedule_id = 2, contract_id = 2, revision = 1, total_amount = 900_000m, generated_at = generated },
                new FeeSchedule { fee_schedule_id = 3, contract_id = 3, revision = 1, total_amount = 1_550_000m, generated_at = generated });
            f.Db.FeeScheduleItems.AddRange(
                Item(PaidItem, 1, new DateOnly(2026, 9, 1), 1_550_000m, FeeItemStatuses.Paid, deposit: 500_000m),
                Item(OverdueItem, 1, new DateOnly(2026, 9, 20), 900_000m, FeeItemStatuses.Overdue),
                Item(PendingItem, 1, new DateOnly(2026, 10, 20), 900_000m, FeeItemStatuses.Pending),
                Item(OtherWardItem, 2, new DateOnly(2026, 10, 1), 900_000m, FeeItemStatuses.Pending),
                Item(ExpiredPaidItem, 3, new DateOnly(2026, 6, 1), 1_550_000m, FeeItemStatuses.Paid, deposit: 500_000m));

            f.Db.ViolationTypes.Add(new ViolationType { violation_type_code = "OUTSIDE_SLOT", description = "Lấn ô", is_active = true });
            f.Db.PenaltyFeeSchedules.Add(new PenaltyFeeSchedule
            {
                penalty_schedule_id = 1, ward_unit_id = 1, violation_type = "OUTSIDE_SLOT", penalty_amount = 500_000m, created_by = 1,
            });
            f.Db.Violations.Add(new Violation
            {
                violation_id = 1, slot_id = 1, contract_id = 1, vendor_id = Vendor, violation_type = "OUTSIDE_SLOT",
                recorded_by = 1, source = "ON_SITE", recorded_at = generated,
            });
            f.Db.Penalties.Add(new Penalty
            {
                penalty_id = 1, violation_id = 1, penalty_schedule_id = 1, amount = 500_000m,
                penalty_status = PenaltyStatuses.Unpaid, created_at = generated,
            });

            await f.Db.SaveChangesAsync();
            f.Db.ChangeTracker.Clear();
            return f;
        }

        private static SidewalkSlot Slot(long id, int zone) => new()
        {
            slot_id = id, zone_id = zone, slot_code = $"S-{id:00}", latitude = 16.06m, longitude = 108.21m,
            slot_status = "ACTIVE", source = "WARD_DEFINED",
        };

        private static FeeScheduleItem Item(
            long id, long scheduleId, DateOnly due, decimal amount, string status, decimal deposit = 0m) => new()
        {
            fee_item_id = id, fee_schedule_id = scheduleId, due_date = due, amount = amount, item_status = status,
            deposit_amount = deposit,
            paid_at = status == FeeItemStatuses.Paid ? due.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) : null,
        };

        public void Dispose()
        {
            Db.Dispose();
            Connection.Dispose();
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TestContext(DbContextOptions<StreetBizDbContext> options) : StreetBizDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            foreach (var entity in modelBuilder.Model.GetEntityTypes())
                foreach (var property in entity.GetProperties())
                {
                    if (property.GetComputedColumnSql() is not null)
                    {
                        property.SetComputedColumnSql(null);
                        property.ValueGenerated = ValueGenerated.Never;
                    }
                    property.SetDefaultValueSql(null);
                }
        }
    }
}
