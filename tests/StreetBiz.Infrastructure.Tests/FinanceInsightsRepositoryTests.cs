using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using StreetBiz.Application.Common.Security;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Persistence.Repositories;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Tests;

/// <summary>The queries behind vendor fee schedules, ward collections and arrival notices.</summary>
public sealed class FinanceInsightsRepositoryTests
{
    // Vendor 101 rents slot 1 (zone 1, ward 1) under contract 1; vendor 102 rents slot 2 (zone 2, ward 1);
    // vendor 103 rents slot 3 in ward 2.
    private static readonly DateTime T0 = new(2026, 9, 1, 3, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task A_vendor_sees_only_their_current_schedules_numbered_with_their_invoices()
    {
        using var f = await Fixture.Create();

        var contracts = await new VendorFinanceRepository(f.Db).ListContractsAsync(101, default);
        var items = await new VendorFinanceRepository(f.Db).ListScheduleItemsAsync(101, null, default);

        var contract = Assert.Single(contracts);
        Assert.Equal(("S-01", "Zone 1", "Hải Châu 1", "12 Bạch Đằng", 3_000_000m),
            (contract.SlotCode, contract.ZoneName, contract.WardName, contract.Address, contract.ScheduleTotal));
        // Revision 2 only (the superseded revision 1 is gone), numbered 1..3 by due date.
        Assert.Equal([(1, 3), (2, 3), (3, 3)], items.Select(i => (i.Ordinal, i.OfCount)));
        Assert.Equal("HD-2026-000001", items[0].InvoiceNumber);
        Assert.Null(items[1].InvoiceNumber);
        Assert.Single(await new VendorFinanceRepository(f.Db).ListScheduleItemsAsync(101, 1, default), i => i.ItemStatus == FeeItemStatuses.Paid);
        Assert.Empty(await new VendorFinanceRepository(f.Db).ListScheduleItemsAsync(101, 2, default));
        Assert.Equal(("Vendor 101", "Quán 1"), await new VendorFinanceRepository(f.Db).GetVendorNamesAsync(101, default));
    }

    [Fact]
    public async Task The_ward_sees_its_unpaid_current_instalments_with_who_owes_them()
    {
        using var f = await Fixture.Create();
        var repo = new WardCollectionRepository(f.Db);

        var unpaid = await repo.ListUnpaidFeeItemsAsync(1, default);

        Assert.Equal([12L, 13L, 21L], unpaid.Select(i => i.FeeItemId).Order());
        var item = unpaid.Single(i => i.FeeItemId == 21);
        Assert.Equal((2L, 102L, "Vendor 102", "Quán 2", 2, "Zone 2", "S-02"),
            (item.ContractId, item.VendorUserId, item.VendorName, item.BusinessName, item.ZoneId, item.ZoneName, item.SlotCode));
        Assert.Single(await repo.ListUnpaidFeeItemsAsync(2, default));
    }

    [Fact]
    public async Task Activity_includes_superseded_payments_and_flags_them_not_current()
    {
        using var f = await Fixture.Create();
        var repo = new WardCollectionRepository(f.Db);

        var activity = await repo.ListFeeActivityAsync(
            1, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), T0, T0.AddDays(30), default);

        // Item 1 (superseded revision, paid in the window) counts as revenue but not as due.
        Assert.Contains(activity, row => row.IsCurrent == false && row.ItemStatus == FeeItemStatuses.Paid);
        Assert.Equal(3, activity.Count(row => row.IsCurrent));

        var zones = await repo.ListZonesAsync(1, default);
        Assert.Equal([(1, "Zone 1", 1, 1), (2, "Zone 2", 1, 1)],
            zones.OrderBy(z => z.ZoneId).Select(z => (z.ZoneId, z.ZoneName, z.SlotCount, z.RentedSlots)));
    }

    [Fact]
    public async Task A_reminder_is_a_notification_to_the_vendor_plus_an_audit_entry_and_is_found_again()
    {
        using var f = await Fixture.Create();
        var repo = new WardCollectionRepository(f.Db);

        await repo.SendDebtReminderAsync(1, 101, 1, "Phường nhắc", "Ô S-01 đang nợ", T0.AddDays(3), default);
        await repo.SendDebtReminderAsync(1, 101, 1, "Phường nhắc", "Ô S-01 đang nợ", T0.AddDays(5), default);

        var last = await repo.GetLastDebtRemindersAsync([1, 2], default);
        Assert.Equal(T0.AddDays(5), Assert.Single(last).Value);
        Assert.Equal(2, await f.Db.AuditLogs.CountAsync(log => log.action == "FEE_DEBT_REMINDER_SENT"));
        Assert.Equal(101, (await f.Db.Notifications.FirstAsync()).user_id);
    }

    [Fact]
    public async Task Arrival_notices_reach_the_stall_and_only_open_orders_are_listed()
    {
        using var f = await Fixture.Create();
        f.Db.Orders.AddRange(
            Order(6, OrderStatuses.Preparing),
            Order(7, OrderStatuses.Completed));
        await f.Db.SaveChangesAsync();
        var tracking = new OrderTrackingRepository(f.Db);

        var target = await tracking.GetArrivalTargetAsync(50, 6, default);
        Assert.Equal(("SB-6", 101L, (DateTime?)null), (target!.OrderCode, target.VendorUserId, target.LastNotifiedAt));
        Assert.Null(await tracking.GetArrivalTargetAsync(51, 6, default));

        await tracking.RecordArrivalAsync(6, 101, "Khách đang đến", "khoảng 5 phút", T0, default);
        await tracking.RecordArrivalAsync(6, 101, "Khách đang đến", "khoảng 2 phút", T0.AddMinutes(3), default);
        await tracking.RecordArrivalAsync(7, 101, "Khách đang đến", "đã xong", T0, default);

        Assert.Equal(T0.AddMinutes(3), (await tracking.GetArrivalTargetAsync(50, 6, default))!.LastNotifiedAt);
        var arrivals = await tracking.ListVendorArrivalsAsync(101, T0.AddMinutes(-1), default);
        var only = Assert.Single(arrivals);
        Assert.Equal((6L, "khoảng 2 phút"), (only.OrderId, only.Message));
        Assert.Empty(await tracking.ListVendorArrivalsAsync(102, T0.AddMinutes(-1), default));
    }

    private static Order Order(long id, string status) => new()
    {
        order_id = id, order_code = $"SB-{id}", customer_user_id = 50, storefront_id = 1, order_status = status,
        subtotal_amount = 30_000, total_amount = 30_000, placed_at = T0, created_at = T0,
    };

    private sealed class Fixture : IDisposable
    {
        private SqliteConnection Connection { get; } = new("Data Source=:memory:");
        public TestContext Db { get; private set; } = null!;

        public static async Task<Fixture> Create()
        {
            var f = new Fixture();
            await f.Connection.OpenAsync();
            f.Db = new TestContext(new DbContextOptionsBuilder<StreetBizDbContext>().UseSqlite(f.Connection).Options);
            await f.Db.Database.EnsureCreatedAsync();

            f.Db.Roles.AddRange(
                new Role { role_code = "PLATFORM_ADMIN", role_name = "Admin" },
                new Role { role_code = "VENDOR", role_name = "Vendor" },
                new Role { role_code = "CUSTOMER", role_name = "Customer" });
            f.Db.AdministrativeUnits.AddRange(
                new AdministrativeUnit { unit_id = 1, unit_type = "WARD", unit_name = "Hải Châu 1" },
                new AdministrativeUnit { unit_id = 2, unit_type = "WARD", unit_name = "Thạch Thang" });
            f.Db.UserAccounts.AddRange(Account(1, "PLATFORM_ADMIN"), Account(50, "CUSTOMER"), Account(51, "CUSTOMER"));
            foreach (var (zone, ward) in new[] { (1, 1), (2, 1), (3, 2) })
                f.Db.PricingZones.Add(new PricingZone
                {
                    zone_id = zone, ward_unit_id = ward, ward_unit_type = "WARD", zone_name = $"Zone {zone}",
                    zone_code = $"Z-{zone}", price_per_day = 25000, created_by = 1,
                });

            f.Rental(1, zone: 1);
            f.Rental(2, zone: 2);
            f.Rental(3, zone: 3);

            // Contract 1: revision 1 superseded (one instalment paid on it), revision 2 current with 3 items.
            f.Db.FeeSchedules.AddRange(
                new FeeSchedule { fee_schedule_id = 10, contract_id = 1, revision = 1, total_amount = 1_000_000, generated_at = T0, superseded_at = T0.AddDays(1) },
                new FeeSchedule { fee_schedule_id = 11, contract_id = 1, revision = 2, total_amount = 3_000_000, generated_at = T0.AddDays(1) },
                new FeeSchedule { fee_schedule_id = 20, contract_id = 2, revision = 1, total_amount = 1_000_000, generated_at = T0 },
                new FeeSchedule { fee_schedule_id = 30, contract_id = 3, revision = 1, total_amount = 1_000_000, generated_at = T0 });
            f.Db.FeeScheduleItems.AddRange(
                Instalment(1, 10, new DateOnly(2026, 9, 3), FeeItemStatuses.Paid, T0.AddDays(1)),
                Instalment(11, 11, new DateOnly(2026, 9, 5), FeeItemStatuses.Paid, T0.AddDays(2)),
                Instalment(12, 11, new DateOnly(2026, 9, 25), FeeItemStatuses.Overdue),
                Instalment(13, 11, new DateOnly(2026, 10, 25), FeeItemStatuses.Pending),
                Instalment(21, 20, new DateOnly(2026, 9, 20), FeeItemStatuses.Overdue),
                Instalment(31, 30, new DateOnly(2026, 9, 20), FeeItemStatuses.Overdue));
            f.Db.Invoices.Add(new Invoice
            {
                invoice_id = 1, invoice_number = "HD-2026-000001", fee_item_id = 11, vendor_id = 101,
                amount = 1_000_000, issued_at = T0.AddDays(2),
            });
            await f.Db.SaveChangesAsync();
            return f;
        }

        private static FeeScheduleItem Instalment(long id, long schedule, DateOnly due, string status, DateTime? paidAt = null) =>
            new() { fee_item_id = id, fee_schedule_id = schedule, due_date = due, amount = 1_000_000, item_status = status, paid_at = paidAt };

        private static UserAccount Account(long id, string role) => new()
        {
            user_id = id, phone_number = $"09000{id:000}", password_hash = "test-only", full_name = $"User {id}",
            role_code = role, ward_unit_id = 1, account_status = "ACTIVE",
        };

        /// <summary>Vendor 100+id with an approved registration, slot id in the zone, contract id, storefront id.</summary>
        private void Rental(long id, int zone)
        {
            var userId = 100 + id;
            var account = Account(userId, "VENDOR");
            account.full_name = $"Vendor {userId}";
            Db.UserAccounts.Add(account);
            Db.Vendors.Add(new Vendor { vendor_id = userId, user_id = userId });
            Db.BusinessRegistrations.Add(new BusinessRegistration
            {
                registration_id = id, vendor_id = userId, ward_unit_id = 1, vendor_type = "FIXED_STOREFRONT",
                display_name = $"Quán {id}", registration_status = "APPROVED", declared_address = "12 Bạch Đằng",
            });
            Db.SidewalkSlots.Add(new SidewalkSlot
            {
                slot_id = id, zone_id = zone, slot_code = $"S-{id:00}", latitude = 16.06m, longitude = 108.21m,
                slot_status = "ACTIVE", source = "WARD_DEFINED",
            });
            Db.RentalApplications.Add(new RentalApplication
            {
                application_id = id, registration_id = id, slot_id = id, application_method = "MANUAL_SELECTED",
                requested_term_days = 90, application_status = "APPROVED",
            });
            Db.RentalContracts.Add(new RentalContract
            {
                contract_id = id, application_id = id, slot_id = id, vendor_id = userId,
                start_date = new DateOnly(2026, 8, 1), end_date = new DateOnly(2026, 10, 30), contract_status = "ACTIVE",
            });
            Db.Storefronts.Add(new Storefront
            {
                storefront_id = id, registration_id = id, contract_id = id, storefront_name = $"Quán {id}",
                availability_status = "OPEN",
            });
        }

        public void Dispose()
        {
            Db.Dispose();
            Connection.Dispose();
        }
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
