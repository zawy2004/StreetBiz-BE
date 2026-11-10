using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Options;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Infrastructure.Payments;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Persistence.Repositories;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;
using StreetBiz.Infrastructure.Security;

namespace StreetBiz.Infrastructure.Tests;

/// <summary>
/// An SQLite database with what payments to payees need: ward 1 (slot S-01, an instalment) and
/// ward 2 with their officers, household 7 (registered in ward 1, storefront 1) and its user, a
/// customer with a cart at storefront 1, and a second household 8 with storefront 2.
/// </summary>
internal sealed class PayeeTestFixture : IDisposable
{
    public const long Officer = 1;
    public const long OtherWardOfficer = 2;
    public const long VendorUser = 7;
    public const long Vendor = 7;
    public const long OtherVendorUser = 8;
    public const long OtherVendor = 8;
    public const long Customer = 9;
    public const int Ward = 1;
    public const int OtherWard = 2;
    public const long FeeItem = 3;
    public const long Storefront = 1;
    public const long OtherStorefront = 2;
    public const long Cart = 1;
    public const long Penalty = 1;
    public const long OtherWardPenalty = 2;
    public const long OrderId = 5;

    public static readonly DateTimeOffset Now = new(2026, 11, 2, 3, 0, 0, TimeSpan.Zero);

    /// <summary>A fixed 32-byte key, so tests can check what is stored without a configured secret.</summary>
    public static readonly byte[] CredentialKey = [.. Enumerable.Range(1, 32).Select(i => (byte)i)];

    private SqliteConnection Connection { get; } = new("Data Source=:memory:");

    public TestContext Db { get; private set; } = null!;

    public AesGcmCredentialProtector Protector { get; } = new(CredentialKey);

    public TimeProvider Clock { get; set; } = new FixedClock(Now);

    public PayeeAccountRepository Payees => new(Db, Protector);

    public PaymentTransactionRepository Transactions => new(Db);

    public FinanceRepository Finance(PaymentGatewaySettings? settings = null) =>
        new(Db, Clock, Options.Create(settings ?? new PaymentGatewaySettings()));

    public static async Task<PayeeTestFixture> Create()
    {
        var f = new PayeeTestFixture();
        await f.Connection.OpenAsync();
        f.Db = new TestContext(new DbContextOptionsBuilder<StreetBizDbContext>().UseSqlite(f.Connection).Options);
        await f.Db.Database.EnsureCreatedAsync();
        var db = f.Db;

        db.Roles.AddRange(
            new Role { role_code = "WARD_AUTHORITY", role_name = "Ward" },
            new Role { role_code = "VENDOR", role_name = "Vendor" },
            new Role { role_code = "CUSTOMER", role_name = "Customer" });
        db.AdministrativeUnits.AddRange(
            new AdministrativeUnit { unit_id = Ward, unit_type = "WARD", unit_name = "Phường Hải Châu" },
            new AdministrativeUnit { unit_id = OtherWard, unit_type = "WARD", unit_name = "Phường Thanh Khê" });
        db.UserAccounts.AddRange(
            User(Officer, "WARD_AUTHORITY", Ward),
            User(OtherWardOfficer, "WARD_AUTHORITY", OtherWard),
            User(VendorUser, "VENDOR", Ward),
            User(OtherVendorUser, "VENDOR", Ward),
            User(Customer, "CUSTOMER", null));
        db.Vendors.AddRange(
            new Vendor { vendor_id = Vendor, user_id = VendorUser },
            new Vendor { vendor_id = OtherVendor, user_id = OtherVendorUser });
        db.PricingZones.Add(new PricingZone
        {
            zone_id = 1, ward_unit_id = Ward, ward_unit_type = "WARD", zone_name = "Zone", price_per_day = 25000, created_by = Officer,
        });
        db.SidewalkSlots.AddRange(
            new SidewalkSlot { slot_id = 1, zone_id = 1, slot_code = "S-01", latitude = 16.06m, longitude = 108.21m, slot_status = "ACTIVE", source = "WARD_DEFINED" },
            new SidewalkSlot { slot_id = 2, zone_id = 1, slot_code = "S-02", latitude = 16.07m, longitude = 108.22m, slot_status = "ACTIVE", source = "WARD_DEFINED" });
        db.BusinessRegistrations.AddRange(
            new BusinessRegistration { registration_id = 1, vendor_id = Vendor, ward_unit_id = Ward, vendor_type = "ITINERANT", display_name = "Bánh mì Cô Lan", registration_status = "APPROVED" },
            new BusinessRegistration { registration_id = 2, vendor_id = OtherVendor, ward_unit_id = Ward, vendor_type = "ITINERANT", display_name = "Xe nước ép", registration_status = "APPROVED" });
        foreach (var id in new long[] { 1, 2 })
        {
            db.RentalApplications.Add(new RentalApplication
            {
                application_id = id, registration_id = id, slot_id = id, application_method = "MANUAL_SELECTED",
                requested_term_days = 120, application_status = "APPROVED",
            });
            db.RentalContracts.Add(new RentalContract
            {
                contract_id = id, application_id = id, slot_id = id, vendor_id = id == 1 ? Vendor : OtherVendor,
                start_date = new DateOnly(2026, 9, 1), end_date = new DateOnly(2026, 12, 29), contract_status = "ACTIVE",
            });
            db.Storefronts.Add(new Storefront
            {
                storefront_id = id, registration_id = id, contract_id = id,
                storefront_name = id == 1 ? "Bánh mì Cô Lan" : "Xe nước ép", availability_status = "OPEN",
                created_at = Now.UtcDateTime,
            });
        }

        db.FeeSchedules.Add(new FeeSchedule
        {
            fee_schedule_id = 1, contract_id = 1, revision = 1, total_amount = 3_000_000m, generated_at = Now.UtcDateTime.AddDays(-60),
        });
        db.FeeScheduleItems.Add(new FeeScheduleItem
        {
            fee_item_id = FeeItem, fee_schedule_id = 1, due_date = new DateOnly(2026, 11, 10), amount = 1_040_000m,
            item_status = FeeItemStatuses.Pending,
        });
        db.ShoppingCarts.Add(new ShoppingCart
        {
            cart_id = Cart, customer_user_id = Customer, storefront_id = Storefront, cart_status = "ACTIVE", created_at = Now.UtcDateTime,
        });
        // A fine for household 7 in ward 1, and one in ward 2 (recorded by that ward's officer, no slot).
        db.ViolationTypes.Add(new ViolationType { violation_type_code = "BLOCK_PEDESTRIAN", description = "Cản trở lối đi bộ", is_active = true });
        db.PenaltyFeeSchedules.AddRange(
            new PenaltyFeeSchedule
            {
                penalty_schedule_id = 1, ward_unit_id = Ward, ward_unit_type = "WARD", violation_type = "BLOCK_PEDESTRIAN",
                penalty_amount = 500_000m, effective_from = new DateOnly(2026, 1, 1), created_by = Officer, creator_role = "WARD_AUTHORITY",
                created_at = Now.UtcDateTime.AddDays(-90),
            },
            new PenaltyFeeSchedule
            {
                penalty_schedule_id = 2, ward_unit_id = OtherWard, ward_unit_type = "WARD", violation_type = "BLOCK_PEDESTRIAN",
                penalty_amount = 500_000m, effective_from = new DateOnly(2026, 1, 1), created_by = OtherWardOfficer, creator_role = "WARD_AUTHORITY",
                created_at = Now.UtcDateTime.AddDays(-90),
            });
        db.Violations.AddRange(
            new Violation
            {
                violation_id = 1, slot_id = 1, contract_id = 1, vendor_id = Vendor, violation_type = "BLOCK_PEDESTRIAN",
                recorded_by = Officer, recorder_role = "WARD_AUTHORITY", source = "ON_SITE", recorded_at = Now.UtcDateTime.AddDays(-3),
                bien_ban_so = "0001/BB-VPHC",
            },
            new Violation
            {
                violation_id = 2, vendor_id = Vendor, violation_type = "BLOCK_PEDESTRIAN",
                recorded_by = OtherWardOfficer, recorder_role = "WARD_AUTHORITY", source = "ON_SITE", recorded_at = Now.UtcDateTime.AddDays(-3),
                bien_ban_so = "0002/BB-VPHC",
            });
        db.Penalties.AddRange(
            new Penalty
            {
                penalty_id = Penalty, violation_id = 1, penalty_schedule_id = 1, amount = 500_000m, penalty_status = "UNPAID",
                decision_number = "QĐXP-2026/0001", created_at = Now.UtcDateTime.AddDays(-2),
            },
            new Penalty
            {
                penalty_id = OtherWardPenalty, violation_id = 2, penalty_schedule_id = 2, amount = 500_000m, penalty_status = "UNPAID",
                decision_number = "QĐXP-2026/0002", created_at = Now.UtcDateTime.AddDays(-2),
            });
        // An order of the customer at household 7's storefront, waiting for its payment.
        db.Orders.Add(new StreetBiz.Infrastructure.Persistence.ScaffoldedModels.Order
        {
            order_id = OrderId, order_code = "SB-20261102-ABC", customer_user_id = Customer, storefront_id = Storefront,
            order_status = "PENDING_PAYMENT", subtotal_amount = 65_000m, total_amount = 65_000m, created_at = Now.UtcDateTime,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return f;
    }

    /// <summary>Connects a receiving account through the repository, as the connect command does.</summary>
    public Task<PayeeAccountRow> ConnectVendorAsync(long vendorId = Vendor, string token = "token-vendor", string checksum = "vendor-checksum") =>
        Payees.SaveAsync(
            new SavePayeeAccount(PayeeOwnerTypes.Vendor, vendorId, null, $"client-{vendorId}", $"api-{vendorId}", checksum, token,
                new PayeeChannelInfo("MB Bank", "0123456789", "NGUYEN THI LAN", true)),
            vendorId == Vendor ? VendorUser : OtherVendorUser, Now.UtcDateTime, default);

    public Task<PayeeAccountRow> ConnectWardAsync(int wardId = Ward, string token = "token-ward", string checksum = "ward-checksum") =>
        Payees.SaveAsync(
            new SavePayeeAccount(PayeeOwnerTypes.Ward, null, wardId, $"client-ward-{wardId}", $"api-ward-{wardId}", checksum, token,
                new PayeeChannelInfo("Vietcombank", "9988776655", "UBND PHUONG", true)),
            wardId == Ward ? Officer : OtherWardOfficer, Now.UtcDateTime, default);

    private static UserAccount User(long id, string role, int? ward) => new()
    {
        user_id = id,
        phone_number = $"09000000{id:00}",
        password_hash = "test-only",
        full_name = $"User {id}",
        role_code = role,
        ward_unit_id = ward,
        account_status = "ACTIVE",
    };

    public void Dispose()
    {
        Db.Dispose();
        Connection.Dispose();
    }

    public sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>SQLite has no computed columns or SQL defaults: tests set those values themselves.</summary>
    public sealed class TestContext(DbContextOptions<StreetBizDbContext> options) : StreetBizDbContext(options)
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
