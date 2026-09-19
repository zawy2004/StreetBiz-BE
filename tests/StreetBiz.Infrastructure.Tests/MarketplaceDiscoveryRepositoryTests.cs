using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using StreetBiz.Application.Common.Geo;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Persistence.Repositories;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Tests;

/// <summary>What a customer may see of the marketplace, and how the discovery filters narrow it (DISC-02..06).</summary>
public sealed class MarketplaceDiscoveryRepositoryTests
{
    private static readonly MarketplaceOpenAt SaturdayMorning = new(6, new TimeOnly(10, 0));

    // Store 1: ward 1, open Saturday 06-11, rated. Store 2: ward 1, open Saturday 16-22. Store 3: ward 2, no hours, far away.
    // Stores 4-6 must never be visible: switched off, contract no longer active, account suspended.
    private const long BreakfastStall = 1;
    private const long EveningCafe = 2;
    private const long NoonNoodles = 3;

    [Fact]
    public async Task Only_open_storefronts_with_a_live_contract_an_approved_registration_and_an_active_account_are_listed()
    {
        using var f = await Fixture.Create();

        var rows = await f.Repository.ListStorefrontsAsync(NoFilter, 50, default);

        Assert.Equal(new long[] { BreakfastStall, EveningCafe, NoonNoodles }, rows.Select(r => r.StorefrontId));
        Assert.Null(await f.Repository.GetStorefrontAsync(4, default));
        Assert.Null(await f.Repository.GetStorefrontAsync(5, default));
        Assert.Null(await f.Repository.GetStorefrontAsync(6, default));
    }

    [Fact]
    public async Task A_row_carries_its_ward_location_rating_menu_summary_and_opening_hours()
    {
        using var f = await Fixture.Create();

        var row = (await f.Repository.ListStorefrontsAsync(NoFilter, 50, default)).Single(r => r.StorefrontId == BreakfastStall);

        Assert.Equal("Breakfast Stall", row.StorefrontName);
        Assert.Equal((1, "One"), (row.WardId, row.WardName));
        Assert.Equal("S-01", row.SlotCode);
        Assert.Equal(4.5m, row.CommunityRating);
        Assert.Equal(3, row.CommunityCount);
        // The hidden dessert is not on the public menu; the sold-out item still is.
        Assert.Equal(2, row.MenuItemCount);
        Assert.Equal(25_000m, row.MinPrice);
        Assert.Equal(["Mains"], row.Categories);
        Assert.Equal(new StorefrontHourRow(6, new TimeOnly(6, 0), new TimeOnly(11, 0)), Assert.Single(row.Hours));

        var unrated = (await f.Repository.ListStorefrontsAsync(NoFilter, 50, default)).Single(r => r.StorefrontId == EveningCafe);
        Assert.Null(unrated.CommunityRating);
        Assert.Equal(0, unrated.CommunityCount);
    }

    [Fact]
    public async Task The_ward_category_and_text_filters_narrow_the_list()
    {
        using var f = await Fixture.Create();

        Assert.Equal(new long[] { NoonNoodles }, await Ids(f, NoFilter with { WardId = 2 }));
        Assert.Equal(new long[] { BreakfastStall, NoonNoodles }, await Ids(f, NoFilter with { CategoryId = 1 }));
        Assert.Equal(new long[] { EveningCafe }, await Ids(f, NoFilter with { CategoryId = 3 }));
        Assert.Equal(new long[] { BreakfastStall }, await Ids(f, NoFilter with { Query = "Breakfast" })); // storefront name
        Assert.Equal(new long[] { BreakfastStall }, await Ids(f, NoFilter with { Query = "Sticky" })); // menu item name
        Assert.Equal(new long[] { EveningCafe }, await Ids(f, NoFilter with { Query = "Drinks" })); // category name
        Assert.Equal(new long[] { NoonNoodles }, await Ids(f, NoFilter with { Query = "Riverside" })); // storefront description
        Assert.Empty(await Ids(f, NoFilter with { Query = "Hidden dessert" }));
    }

    [Fact]
    public async Task Open_now_keeps_storefronts_inside_a_window_and_those_with_no_published_hours()
    {
        using var f = await Fixture.Create();

        Assert.Equal(new long[] { BreakfastStall, NoonNoodles }, await Ids(f, NoFilter with { OpenAt = SaturdayMorning }));
        Assert.Equal(new long[] { EveningCafe, NoonNoodles }, await Ids(f, NoFilter with { OpenAt = new(6, new TimeOnly(17, 0)) }));
        Assert.Equal(new long[] { NoonNoodles }, await Ids(f, NoFilter with { OpenAt = new(2, new TimeOnly(10, 0)) })); // no window on a Tuesday
    }

    [Fact]
    public async Task The_bounding_box_keeps_only_storefronts_inside_it()
    {
        using var f = await Fixture.Create();

        var nearHere = GeoMath.BoundingBox(16.0600, 108.2140, 2_000);

        Assert.Equal(new long[] { BreakfastStall, EveningCafe }, await Ids(f, NoFilter with { Area = nearHere }));
    }

    [Fact]
    public async Task Storefront_detail_lists_the_public_menu_only()
    {
        using var f = await Fixture.Create();

        var detail = await f.Repository.GetStorefrontAsync(BreakfastStall, default);

        Assert.Equal(["Sticky rice", "Sweet rice"], detail!.Items.Select(i => i.ItemName));
        Assert.Contains(detail.Items, i => i.AvailabilityStatus == "SOLD_OUT");
    }

    [Fact]
    public async Task Service_areas_and_categories_reflect_only_what_is_visible()
    {
        using var f = await Fixture.Create();

        var locations = await f.Repository.ListStorefrontLocationsAsync(default);
        var categories = await f.Repository.ListMarketplaceCategoriesAsync(default);

        Assert.Equal([(1, "One", "District"), (1, "One", "District"), (2, "Two", "District")],
            locations.Select(l => (l.WardId, l.WardName, l.DistrictName!)).OrderBy(l => l.WardId));
        Assert.Equal([("Drinks", 1), ("Mains", 3)], categories.Select(c => (c.CategoryName, c.ItemCount)));
    }

    [Fact]
    public async Task Menu_search_filters_by_ward_category_price_and_opening_hours()
    {
        using var f = await Fixture.Create();
        var all = new MarketplaceMenuFilter(null, null, null, null, null, null, MarketplaceMenuSorts.Name);

        async Task<string[]> Names(MarketplaceMenuFilter filter) =>
            (await f.Repository.SearchMenuItemsAsync(filter, 50, default)).Select(i => i.ItemName).ToArray();

        // The default order is storefront name, then item name.
        Assert.Equal(["Sticky rice", "Sweet rice", "Coffee", "Noodles"], await Names(all));
        Assert.Equal(["Noodles"], await Names(all with { WardId = 2 }));
        Assert.Equal(["Coffee"], await Names(all with { CategoryId = 3 }));
        Assert.Equal(["Sticky rice", "Sweet rice"], await Names(all with { MinPrice = 25_000, MaxPrice = 30_000 }));
        Assert.Equal(["Sticky rice", "Sweet rice", "Noodles"], await Names(all with { OpenAt = SaturdayMorning }));
        // Sorting by price is covered by the SQL Server smoke test: SQLite cannot ORDER BY a decimal column.
    }

    [Fact]
    public async Task Menu_search_no_longer_finds_items_of_storefronts_that_are_not_public()
    {
        using var f = await Fixture.Create();

        var hits = await f.Repository.SearchMenuItemsAsync(
            new MarketplaceMenuFilter("Ghost", null, null, null, null, null, MarketplaceMenuSorts.Name), 50, default);

        Assert.Empty(hits);
        Assert.Null(await f.Repository.GetMenuItemAsync(900, default));
    }

    private static readonly MarketplaceStorefrontFilter NoFilter = new(null, null, null, null, null);

    private static async Task<long[]> Ids(Fixture f, MarketplaceStorefrontFilter filter) =>
        (await f.Repository.ListStorefrontsAsync(filter, 50, default)).Select(r => r.StorefrontId).Order().ToArray();

    private sealed class Fixture : IDisposable
    {
        private SqliteConnection Connection { get; } = new("Data Source=:memory:");
        public TestContext Db { get; private set; } = null!;
        public CommerceRepository Repository { get; private set; } = null!;

        public static async Task<Fixture> Create()
        {
            var f = new Fixture();
            await f.Connection.OpenAsync();
            f.Db = new TestContext(new DbContextOptionsBuilder<StreetBizDbContext>().UseSqlite(f.Connection).Options);
            await f.Db.Database.EnsureCreatedAsync();
            // EnsureCreated does not build views; only the columns the repository reads are needed.
            await f.Db.Database.ExecuteSqlRawAsync(
                "CREATE VIEW vw_VendorRatings AS SELECT vendor_id, 0 AS verified_count, NULL AS verified_rating, " +
                "3 AS community_count, 4.5 AS community_rating FROM Vendors WHERE vendor_id = 101");
            f.Repository = new CommerceRepository(f.Db, TimeProvider.System);

            f.Db.Roles.AddRange(
                new Role { role_code = "PLATFORM_ADMIN", role_name = "Admin" },
                new Role { role_code = "VENDOR", role_name = "Vendor" });
            f.Db.AdministrativeUnits.AddRange(
                new AdministrativeUnit { unit_id = 10, unit_type = "DISTRICT", unit_name = "District" },
                new AdministrativeUnit { unit_id = 1, unit_type = "WARD", unit_name = "One", parent_unit_id = 10 },
                new AdministrativeUnit { unit_id = 2, unit_type = "WARD", unit_name = "Two", parent_unit_id = 10 });
            f.Db.UserAccounts.Add(Account(1, "PLATFORM_ADMIN", "ACTIVE"));
            f.Db.FoodCategories.AddRange(
                new FoodCategory { category_id = 1, category_name = "Mains", created_by = 1 },
                new FoodCategory { category_id = 2, category_name = "Desserts", created_by = 1 },
                new FoodCategory { category_id = 3, category_name = "Drinks", created_by = 1 });
            foreach (var wardId in new[] { 1, 2 })
                f.Db.PricingZones.Add(new PricingZone
                {
                    zone_id = wardId, ward_unit_id = wardId, ward_unit_type = "WARD", zone_name = $"Zone {wardId}",
                    zone_code = $"Z-{wardId}", price_per_day = 25000, created_by = 1,
                });

            f.AddStorefront(1, "Breakfast Stall", ward: 1, 16.0600m, 108.2140m, hours: [(6, 6, 11)]);
            f.AddStorefront(2, "Evening Cafe", ward: 1, 16.0610m, 108.2150m, hours: [(6, 16, 22)]);
            f.AddStorefront(3, "Noon Noodles", ward: 2, 16.0120m, 108.2400m, description: "Riverside noodle cart");
            f.AddStorefront(4, "Paused Stall", ward: 1, 16.0600m, 108.2140m, availability: "PAUSED");
            f.AddStorefront(5, "Expired Stall", ward: 1, 16.0600m, 108.2140m, contractStatus: "EXPIRED");
            f.AddStorefront(6, "Suspended Stall", ward: 1, 16.0600m, 108.2140m, accountStatus: "SUSPENDED");

            f.Db.MenuItems.AddRange(
                Item(10, 1, 1, "Sticky rice", 30_000, "AVAILABLE"),
                Item(11, 1, 1, "Sweet rice", 25_000, "SOLD_OUT"),
                Item(12, 1, 2, "Hidden dessert", 10_000, "HIDDEN"),
                Item(20, 2, 3, "Coffee", 20_000, "AVAILABLE"),
                Item(30, 3, 1, "Noodles", 45_000, "AVAILABLE"),
                Item(900, 4, 1, "Ghost paused", 5_000, "AVAILABLE"),
                Item(901, 5, 1, "Ghost expired", 5_000, "AVAILABLE"),
                Item(902, 6, 1, "Ghost suspended", 5_000, "AVAILABLE"));
            await f.Db.SaveChangesAsync();
            return f;
        }

        private static UserAccount Account(long id, string role, string status) => new()
        {
            user_id = id, phone_number = $"09000{id:000}", password_hash = "test-only", full_name = $"User {id}",
            role_code = role, ward_unit_id = 1, account_status = status,
        };

        private static MenuItem Item(long id, long storefrontId, int categoryId, string name, decimal price, string status) => new()
        {
            menu_item_id = id, storefront_id = storefrontId, category_id = categoryId, item_name = name,
            unit_price = price, availability_status = status,
        };

        /// <summary>One vendor with an approved registration, a slot in the ward, an active contract on it and a storefront.</summary>
        private void AddStorefront(
            long id, string name, int ward, decimal latitude, decimal longitude,
            string availability = "OPEN", string contractStatus = "ACTIVE", string accountStatus = "ACTIVE",
            string? description = null, (short Day, int From, int To)[]? hours = null)
        {
            var userId = 100 + id;
            Db.UserAccounts.Add(Account(userId, "VENDOR", accountStatus));
            Db.Vendors.Add(new Vendor { vendor_id = userId, user_id = userId });
            Db.BusinessRegistrations.Add(new BusinessRegistration
            {
                registration_id = id, vendor_id = userId, ward_unit_id = ward, vendor_type = "FIXED_STOREFRONT",
                display_name = name, registration_status = "APPROVED",
            });
            Db.SidewalkSlots.Add(new SidewalkSlot
            {
                slot_id = id, zone_id = ward, slot_code = $"S-{id:00}", latitude = latitude, longitude = longitude,
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
                start_date = new DateOnly(2026, 9, 1), end_date = new DateOnly(2026, 12, 1), contract_status = contractStatus,
            });
            Db.Storefronts.Add(new Storefront
            {
                storefront_id = id, registration_id = id, contract_id = id, storefront_name = name,
                description = description, availability_status = availability,
            });
            foreach (var (day, from, to) in hours ?? [])
                Db.StorefrontBusinessHours.Add(new StorefrontBusinessHour
                {
                    storefront_id = id, day_of_week = day, opens_at = new TimeOnly(from, 0), closes_at = new TimeOnly(to, 0),
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
