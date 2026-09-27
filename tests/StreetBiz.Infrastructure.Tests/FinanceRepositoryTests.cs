using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Persistence.Repositories;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Tests;

/// <summary>
/// Contract 1 has a superseded revision (one leftover PENDING item) and a current revision with
/// four instalments: paid, past due, due within the reminder window, and due later. Contract 2
/// has no schedule yet. "Now" is 10:00 on 26/09/2026 in Đà Nẵng.
/// </summary>
public sealed class FinanceRepositoryTests
{
    private const long Vendor = 10;
    private const long SupersededItem = 1;
    private const long PastDueItem = 3;
    private const long DueSoonItem = 4;
    private static readonly DateTimeOffset MorningOf26th = new(2026, 9, 26, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Retrying_a_checkout_with_the_same_key_replays_the_original_transaction()
    {
        using var f = await Fixture.Create();
        var repository = f.Repository();

        var first = await repository.CreateFeeCheckoutAsync(Vendor, PastDueItem, "MOMO", "key-1", default);
        var retry = await repository.CreateFeeCheckoutAsync(Vendor, PastDueItem, "MOMO", "key-1", default);

        Assert.Equal(first.TransactionId, retry.TransactionId);
        Assert.Equal(1, await f.Db.PaymentTransactions.CountAsync(t => t.idempotency_key == "key-1"));
    }

    [Fact]
    public async Task Reusing_a_key_for_a_different_checkout_is_a_conflict_not_a_server_error()
    {
        using var f = await Fixture.Create();
        var repository = f.Repository();
        await repository.CreateFeeCheckoutAsync(Vendor, PastDueItem, "MOMO", "key-1", default);

        await Assert.ThrowsAsync<ConflictException>(() =>
            repository.CreateFeeCheckoutAsync(Vendor, DueSoonItem, "MOMO", "key-1", default));
        await Assert.ThrowsAsync<ConflictException>(() =>
            repository.CreateFeeCheckoutAsync(Vendor, PastDueItem, "ZALOPAY", "key-1", default));
    }

    [Fact]
    public async Task A_second_attempt_on_an_instalment_already_paid_does_not_issue_a_second_invoice()
    {
        using var f = await Fixture.Create();
        var repository = f.Repository();
        var first = await repository.CreateFeeCheckoutAsync(Vendor, PastDueItem, "MOMO", "tab-1", default);
        var second = await repository.CreateFeeCheckoutAsync(Vendor, PastDueItem, "MOMO", "tab-2", default);

        await repository.ConfirmSandboxSuccessAsync(Vendor, first.TransactionId, default);
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            repository.ConfirmSandboxSuccessAsync(Vendor, second.TransactionId, default));

        var invoices = await f.Db.Invoices.AsNoTracking().Where(i => i.fee_item_id == PastDueItem).ToListAsync();
        var invoice = Assert.Single(invoices);
        // Continues from the seeded HD-2026-000010; the 2025 sequence is a different year.
        Assert.Equal("HD-2026-000011", invoice.invoice_number);
    }

    [Fact]
    public async Task Invoice_numbers_follow_the_Vietnamese_calendar_year()
    {
        // 01:00 on 1 January 2027 in Đà Nẵng is still 31 December 2026 in UTC.
        using var f = await Fixture.Create(new DateTimeOffset(2026, 12, 31, 18, 0, 0, TimeSpan.Zero));
        var repository = f.Repository();
        var checkout = await repository.CreateFeeCheckoutAsync(Vendor, DueSoonItem, "MOMO", "new-year", default);

        await repository.ConfirmSandboxSuccessAsync(Vendor, checkout.TransactionId, default);

        Assert.True(await f.Db.Invoices.AnyAsync(i => i.invoice_number == "HD-2027-000001"));
    }

    [Fact]
    public async Task An_instalment_on_a_superseded_schedule_can_neither_be_paid_nor_swept()
    {
        using var f = await Fixture.Create();
        var repository = f.Repository();

        Assert.Null(await repository.GetFeeItemForCheckoutAsync(Vendor, SupersededItem, default));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            repository.CreateFeeCheckoutAsync(Vendor, SupersededItem, "MOMO", "old", default));

        await repository.RunFeeReminderSweepAsync(new DateOnly(2026, 9, 26), default);

        var superseded = await f.Db.FeeScheduleItems.AsNoTracking().SingleAsync(i => i.fee_item_id == SupersededItem);
        Assert.Equal(FeeItemStatuses.Pending, superseded.item_status);
        Assert.False(await f.Db.Notifications.AnyAsync(n => n.related_entity_id == SupersededItem));
    }

    [Fact]
    public async Task The_reminder_sweep_sends_each_notice_once_per_Vietnamese_day()
    {
        using var f = await Fixture.Create();
        var repository = f.Repository();
        var today = new DateOnly(2026, 9, 26);

        var first = await repository.RunFeeReminderSweepAsync(today, default);
        var again = await repository.RunFeeReminderSweepAsync(today, default);

        Assert.Equal(new FeeReminderSweepResult(1, 1), first);
        Assert.Equal(new FeeReminderSweepResult(0, 0), again);
        Assert.Equal(FeeItemStatuses.Overdue,
            (await f.Db.FeeScheduleItems.AsNoTracking().SingleAsync(i => i.fee_item_id == PastDueItem)).item_status);

        var reminder = await f.Db.Notifications.AsNoTracking().SingleAsync(n => n.related_entity_id == DueSoonItem);
        Assert.Equal("Khoản phí 2.000.000 đ của ô S-01 sẽ đến hạn trong 2 ngày (28/09/2026).", reminder.body);
    }

    [Fact]
    public async Task Payment_history_names_the_instalment_not_just_the_slot()
    {
        using var f = await Fixture.Create();
        var repository = f.Repository();
        await repository.CreateFeeCheckoutAsync(Vendor, PastDueItem, "MOMO", "key-1", default);

        var row = Assert.Single(await repository.ListPaymentTransactionsAsync(Vendor, default));

        Assert.Equal("Kỳ 2/4 · Tháng 09/2026", row.ReferenceLabel);
        Assert.Equal("S-01", row.SlotCode);
    }

    [Fact]
    public async Task A_schedule_generated_inside_the_approvers_transaction_rolls_back_with_it()
    {
        using var f = await Fixture.Create();
        var repository = f.Repository();
        FeeInstalment[] instalments = [new(1, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 30), 750_000m)];

        await using (var approval = await f.Db.Database.BeginTransactionAsync())
        {
            var schedule = await repository.ReplaceFeeScheduleAsync(2, 1, 750_000m, instalments, default);
            Assert.Single(schedule.Items);
            await approval.RollbackAsync();
        }

        Assert.False(await f.Db.FeeSchedules.AsNoTracking().AnyAsync(s => s.contract_id == 2));
    }

    private sealed class Fixture : IDisposable
    {
        private SqliteConnection Connection { get; } = new("Data Source=:memory:");
        private TimeProvider Clock { get; init; } = null!;
        public TestContext Db { get; private set; } = null!;

        public FinanceRepository Repository() => new(Db, Clock);

        public static async Task<Fixture> Create(DateTimeOffset? now = null)
        {
            var f = new Fixture { Clock = new FixedClock(now ?? MorningOf26th) };
            await f.Connection.OpenAsync();
            f.Db = new TestContext(new DbContextOptionsBuilder<StreetBizDbContext>().UseSqlite(f.Connection).Options);
            await f.Db.Database.EnsureCreatedAsync();

            f.Db.Roles.AddRange(new Role { role_code = "WARD_AUTHORITY", role_name = "Ward" },
                new Role { role_code = "VENDOR", role_name = "Vendor" });
            f.Db.AdministrativeUnits.Add(new AdministrativeUnit { unit_id = 1, unit_type = "WARD", unit_name = "One" });
            foreach (var id in new long[] { 1, Vendor })
                f.Db.UserAccounts.Add(new UserAccount
                {
                    user_id = id,
                    phone_number = $"09000000{id:00}",
                    password_hash = "test-only",
                    full_name = $"User {id}",
                    role_code = id == 1 ? "WARD_AUTHORITY" : "VENDOR",
                    ward_unit_id = 1,
                    account_status = "ACTIVE",
                });
            f.Db.Vendors.Add(new Vendor { vendor_id = Vendor, user_id = Vendor });
            f.Db.PricingZones.Add(new PricingZone
            {
                zone_id = 1, ward_unit_id = 1, ward_unit_type = "WARD", zone_name = "Zone", price_per_day = 25000, created_by = 1,
            });
            f.Db.SidewalkSlots.AddRange(
                new SidewalkSlot { slot_id = 1, zone_id = 1, slot_code = "S-01", latitude = 16.06m, longitude = 108.21m, slot_status = "ACTIVE", source = "WARD_DEFINED" },
                new SidewalkSlot { slot_id = 2, zone_id = 1, slot_code = "S-02", latitude = 16.07m, longitude = 108.22m, slot_status = "ACTIVE", source = "WARD_DEFINED" });
            f.Db.BusinessRegistrations.Add(new BusinessRegistration
            {
                registration_id = 1, vendor_id = Vendor, ward_unit_id = 1, vendor_type = "ITINERANT", display_name = "Vendor", registration_status = "APPROVED",
            });
            foreach (var id in new long[] { 1, 2 })
            {
                f.Db.RentalApplications.Add(new RentalApplication
                {
                    application_id = id, registration_id = 1, slot_id = id, application_method = "MANUAL_SELECTED",
                    requested_term_days = 120, application_status = "APPROVED",
                });
                f.Db.RentalContracts.Add(new RentalContract
                {
                    contract_id = id, application_id = id, slot_id = id, vendor_id = Vendor,
                    start_date = new DateOnly(2026, 9, 1), end_date = new DateOnly(2026, 12, 29), contract_status = "ACTIVE",
                });
            }

            var generated = new DateTime(2026, 8, 30, 3, 0, 0, DateTimeKind.Utc);
            f.Db.FeeSchedules.AddRange(
                new FeeSchedule { fee_schedule_id = 1, contract_id = 1, revision = 1, total_amount = 1_000_000m, generated_at = generated, superseded_at = generated.AddDays(1) },
                new FeeSchedule { fee_schedule_id = 2, contract_id = 1, revision = 2, total_amount = 6_000_000m, generated_at = generated.AddDays(1) });
            f.Db.FeeScheduleItems.AddRange(
                Item(SupersededItem, 1, new DateOnly(2026, 9, 15), 1_000_000m, FeeItemStatuses.Pending),
                Item(2, 2, new DateOnly(2026, 9, 1), 1_000_000m, FeeItemStatuses.Paid),
                Item(PastDueItem, 2, new DateOnly(2026, 9, 20), 1_000_000m, FeeItemStatuses.Pending),
                Item(DueSoonItem, 2, new DateOnly(2026, 9, 28), 2_000_000m, FeeItemStatuses.Pending),
                Item(5, 2, new DateOnly(2026, 10, 28), 2_000_000m, FeeItemStatuses.Pending));
            f.Db.Invoices.AddRange(
                Invoice(1, "HD-2025-000999"),
                Invoice(2, "HD-2026-000009"),
                Invoice(3, "HD-2026-000010"));
            await f.Db.SaveChangesAsync();
            f.Db.ChangeTracker.Clear();
            return f;
        }

        private static FeeScheduleItem Item(long id, long scheduleId, DateOnly due, decimal amount, string status) => new()
        {
            fee_item_id = id, fee_schedule_id = scheduleId, due_date = due, amount = amount, item_status = status,
            paid_at = status == FeeItemStatuses.Paid ? due.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) : null,
        };

        private static Invoice Invoice(long id, string number) => new()
        {
            invoice_id = id, invoice_number = number, fee_item_id = 2, vendor_id = Vendor, amount = 1_000_000m,
            issued_at = new DateTime(2026, 9, 1, 3, 0, 0, DateTimeKind.Utc),
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
