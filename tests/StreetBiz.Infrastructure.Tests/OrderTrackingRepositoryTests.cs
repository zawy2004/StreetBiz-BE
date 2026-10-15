using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using StreetBiz.Application.Common.Security;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Persistence.Repositories;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Tests;

/// <summary>The queries behind the pickup-range check (ORD-01) and order tracking (ORD-02).</summary>
public sealed class OrderTrackingRepositoryTests
{
    private const long Customer = 50;
    private const long OtherCustomer = 51;
    private const long BunCha = 1;      // 16.0600, 108.2140
    private const long BanhMi = 2;      // 16.0710, 108.2230
    private static readonly DateTime T0 = new(2026, 10, 1, 1, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Checkout_is_measured_against_the_stall_of_the_cart_being_checked_out()
    {
        using var f = await Fixture.Create();
        f.Cart(10, Customer, BunCha, T0);
        f.Cart(11, Customer, BanhMi, T0.AddMinutes(5));
        await f.Db.SaveChangesAsync();

        var point = await f.Repository.GetCheckoutPickupPointAsync(Customer, 10, default);

        Assert.Equal((BunCha, "Bún chả", "12 Bạch Đằng"), (point!.StorefrontId, point.StorefrontName, point.Address));
        Assert.Equal((16.06, 108.214), (point.Latitude, point.Longitude));
    }

    [Fact]
    public async Task Without_a_cart_id_the_newest_active_cart_is_the_one_the_order_will_use()
    {
        using var f = await Fixture.Create();
        f.Cart(10, Customer, BunCha, T0);
        f.Cart(11, Customer, BanhMi, T0.AddMinutes(5));
        f.Cart(12, Customer, BunCha, T0.AddMinutes(9), status: CartStatuses.Abandoned);
        await f.Db.SaveChangesAsync();

        Assert.Equal(BanhMi, (await f.Repository.GetCheckoutPickupPointAsync(Customer, null, default))!.StorefrontId);
        // Another customer's cart, or one no longer active, is never measured against.
        Assert.Null(await f.Repository.GetCheckoutPickupPointAsync(OtherCustomer, 10, default));
        Assert.Null(await f.Repository.GetCheckoutPickupPointAsync(Customer, 12, default));
    }

    [Fact]
    public async Task A_storefronts_pickup_point_is_its_rented_slot()
    {
        using var f = await Fixture.Create();

        var point = await f.Repository.GetStorefrontPickupPointAsync(BanhMi, default);

        Assert.Equal((16.071, 108.223), (point!.Latitude, point.Longitude));
        Assert.Null(await f.Repository.GetStorefrontPickupPointAsync(999, default));
    }

    [Fact]
    public async Task Tracking_reads_milestones_and_the_address_the_order_was_placed_with()
    {
        using var f = await Fixture.Create();
        f.Order(1, Customer, BunCha, OrderStatuses.ReadyForPickup, T0, snapshot: "Old address");
        f.History(1, OrderStatuses.Placed, T0);
        f.History(1, OrderStatuses.Accepted, T0.AddMinutes(2));
        f.History(1, OrderStatuses.Preparing, T0.AddMinutes(3));
        f.History(1, OrderStatuses.ReadyForPickup, T0.AddMinutes(14));
        await f.Db.SaveChangesAsync();

        var row = await f.Repository.GetOrderTrackingAsync(Customer, 1, default);

        Assert.Equal(T0.AddMinutes(2), row!.AcceptedAt);
        Assert.Equal(T0.AddMinutes(14), row.ReadyAt);
        // Labelled UTC, so the JSON instant carries its "Z" and no client reads it as local time.
        Assert.Equal(DateTimeKind.Utc, row.AcceptedAt!.Value.Kind);
        Assert.Equal(DateTimeKind.Utc, row.PlacedAt!.Value.Kind);
        Assert.Equal("Old address", row.PickupPoint.Address);
        // Not being prepared any more: nobody is "ahead" of a ready order.
        Assert.Equal(0, row.OrdersAhead);
        Assert.Null(await f.Repository.GetOrderTrackingAsync(OtherCustomer, 1, default));
    }

    [Fact]
    public async Task Orders_ahead_are_earlier_paid_orders_at_the_same_stall_not_yet_ready()
    {
        using var f = await Fixture.Create();
        f.Order(1, OtherCustomer, BunCha, OrderStatuses.Preparing, T0);                     // ahead
        f.Order(2, OtherCustomer, BunCha, OrderStatuses.Placed, T0.AddMinutes(1));          // ahead
        f.Order(3, OtherCustomer, BunCha, OrderStatuses.ReadyForPickup, T0.AddMinutes(2));  // done cooking
        f.Order(4, OtherCustomer, BunCha, OrderStatuses.PendingPayment, null);              // unpaid
        f.Order(5, OtherCustomer, BanhMi, OrderStatuses.Preparing, T0);                     // other stall
        f.Order(6, Customer, BunCha, OrderStatuses.Accepted, T0.AddMinutes(3));             // this one
        f.Order(7, OtherCustomer, BunCha, OrderStatuses.Placed, T0.AddMinutes(4));          // behind
        f.Order(8, OtherCustomer, BunCha, OrderStatuses.Accepted, T0.AddMinutes(3));        // same instant, larger id: behind
        await f.Db.SaveChangesAsync();

        var row = await f.Repository.GetOrderTrackingAsync(Customer, 6, default);

        Assert.Equal(2, row!.OrdersAhead);
    }

    [Fact]
    public async Task Prep_minutes_are_accepted_to_ready_for_the_stalls_recent_orders_newest_first()
    {
        using var f = await Fixture.Create();
        void Prepared(long orderId, long storefrontId, DateTime acceptedAt, double minutes)
        {
            f.Order(orderId, OtherCustomer, storefrontId, OrderStatuses.Completed, acceptedAt.AddMinutes(-1));
            f.History(orderId, OrderStatuses.Accepted, acceptedAt);
            f.History(orderId, OrderStatuses.ReadyForPickup, acceptedAt.AddMinutes(minutes));
        }
        Prepared(1, BunCha, T0, 12);
        Prepared(2, BunCha, T0.AddHours(1), 8.5);
        Prepared(3, BunCha, T0.AddHours(2), 15);
        Prepared(4, BanhMi, T0.AddHours(3), 30);            // another stall
        Prepared(5, BunCha, T0.AddDays(-40), 20);           // too old
        // Marked ready without ever being accepted (a data gap): no duration can be measured.
        f.Order(6, OtherCustomer, BunCha, OrderStatuses.Completed, T0);
        f.History(6, OrderStatuses.ReadyForPickup, T0.AddHours(4));
        await f.Db.SaveChangesAsync();

        var since = T0.AddDays(-30);
        Assert.Equal([15, 8.5, 12], await f.Repository.GetRecentPrepMinutesAsync(BunCha, since, 10, default));
        Assert.Equal([15, 8.5], await f.Repository.GetRecentPrepMinutesAsync(BunCha, since, 2, default));
        Assert.Empty(await f.Repository.GetRecentPrepMinutesAsync(BunCha, since, 0, default));
    }

    private sealed class Fixture : IDisposable
    {
        private SqliteConnection Connection { get; } = new("Data Source=:memory:");
        public TestContext Db { get; private set; } = null!;
        public OrderTrackingRepository Repository { get; private set; } = null!;

        public static async Task<Fixture> Create()
        {
            var f = new Fixture();
            await f.Connection.OpenAsync();
            f.Db = new TestContext(new DbContextOptionsBuilder<StreetBizDbContext>().UseSqlite(f.Connection).Options);
            await f.Db.Database.EnsureCreatedAsync();
            f.Repository = new OrderTrackingRepository(f.Db);

            f.Db.Roles.AddRange(
                new Role { role_code = "PLATFORM_ADMIN", role_name = "Admin" },
                new Role { role_code = "VENDOR", role_name = "Vendor" },
                new Role { role_code = "CUSTOMER", role_name = "Customer" });
            f.Db.AdministrativeUnits.Add(new AdministrativeUnit { unit_id = 1, unit_type = "WARD", unit_name = "Hải Châu" });
            f.Db.UserAccounts.AddRange(
                Account(1, "PLATFORM_ADMIN"), Account(Customer, "CUSTOMER"), Account(OtherCustomer, "CUSTOMER"));
            f.Db.PricingZones.Add(new PricingZone
            {
                zone_id = 1, ward_unit_id = 1, ward_unit_type = "WARD", zone_name = "Zone", zone_code = "Z-1",
                price_per_day = 25000, created_by = 1,
            });
            f.AddStorefront(BunCha, "Bún chả", "12 Bạch Đằng", 16.0600m, 108.2140m);
            f.AddStorefront(BanhMi, "Bánh mì", "40 Trần Phú", 16.0710m, 108.2230m);
            await f.Db.SaveChangesAsync();
            return f;
        }

        public void Cart(long id, long customerId, long storefrontId, DateTime createdAt, string status = CartStatuses.Active) =>
            Db.ShoppingCarts.Add(new ShoppingCart
            {
                cart_id = id, customer_user_id = customerId, storefront_id = storefrontId,
                cart_status = status, created_at = createdAt,
            });

        public void Order(long id, long customerId, long storefrontId, string status, DateTime? placedAt, string? snapshot = null) =>
            Db.Orders.Add(new Order
            {
                order_id = id, order_code = $"SB-{id}", customer_user_id = customerId, storefront_id = storefrontId,
                order_status = status, subtotal_amount = 30_000, total_amount = 30_000,
                placed_at = placedAt, created_at = placedAt ?? T0, storefront_address_snapshot = snapshot,
            });

        public void History(long orderId, string toStatus, DateTime at) =>
            Db.OrderStatusHistories.Add(new OrderStatusHistory { order_id = orderId, to_status = toStatus, changed_at = at });

        private static UserAccount Account(long id, string role) => new()
        {
            user_id = id, phone_number = $"09000{id:000}", password_hash = "test-only", full_name = $"User {id}",
            role_code = role, ward_unit_id = 1, account_status = "ACTIVE",
        };

        private void AddStorefront(long id, string name, string address, decimal latitude, decimal longitude)
        {
            var userId = 100 + id;
            Db.UserAccounts.Add(Account(userId, "VENDOR"));
            Db.Vendors.Add(new Vendor { vendor_id = userId, user_id = userId });
            Db.BusinessRegistrations.Add(new BusinessRegistration
            {
                registration_id = id, vendor_id = userId, ward_unit_id = 1, vendor_type = "FIXED_STOREFRONT",
                display_name = name, registration_status = "APPROVED", declared_address = address,
            });
            Db.SidewalkSlots.Add(new SidewalkSlot
            {
                slot_id = id, zone_id = 1, slot_code = $"S-{id:00}", latitude = latitude, longitude = longitude,
                slot_status = "ACTIVE", source = "WARD_DEFINED",
            });
            Db.RentalApplications.Add(new RentalApplication
            {
                application_id = id, registration_id = id, slot_id = id, application_method = "MANUAL_SELECTED",
                requested_term_days = 30, application_status = "APPROVED",
            });
            Db.RentalContracts.Add(new RentalContract
            {
                contract_id = id, application_id = id, slot_id = id, vendor_id = userId,
                start_date = new DateOnly(2026, 9, 1), end_date = new DateOnly(2026, 12, 1), contract_status = "ACTIVE",
            });
            Db.Storefronts.Add(new Storefront
            {
                storefront_id = id, registration_id = id, contract_id = id, storefront_name = name,
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
