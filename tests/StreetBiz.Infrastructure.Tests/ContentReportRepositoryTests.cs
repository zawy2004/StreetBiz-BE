using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.Commerce;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Persistence.Repositories;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Tests;

/// <summary>REV-03 on a real (SQLite) schema, including that the new report shows up in the ADM-03 queue.</summary>
public sealed class ContentReportRepositoryTests
{
    private const long Seller = 1;
    private const long Buyer = 2;
    private const long OtherBuyer = 3;
    private const long Admin = 4;

    private static ContentReportDraft Draft(string type, long id, long reporter = Buyer, string reason = "Sai món") =>
        new(type, id, reporter, reason);

    [Fact]
    public async Task A_report_lands_in_the_admin_queue_as_pending_with_an_audit_row()
    {
        using var f = await Fixture.Create();

        var result = await f.Repository.CreateAsync(Draft("STOREFRONT", 1, reason: "Hàng giả, gian lận: bán đồ nhái"), default);

        Assert.Equal(ContentReportOutcome.Created, result.Outcome);
        var queue = await new PlatformAdministrationRepository(f.Db, TimeProvider.System)
            .ListReportedContentAsync(ReportedContentStatuses.Pending, 1, 50, default);
        var row = Assert.Single(queue.Items);
        Assert.Equal(result.ReportId, row.ReportId);
        Assert.Equal("STOREFRONT", row.ContentType);
        Assert.Equal("Quán Một", row.ContentTitle);
        Assert.Equal("Hàng giả, gian lận: bán đồ nhái", row.Reason);
        Assert.Equal("Khách A", row.ReporterName);
        var audit = Assert.Single(f.Db.AuditLogs.Where(a => a.action == "CONTENT_REPORT_CREATE"));
        Assert.Equal(result.ReportId, audit.entity_id);
        Assert.Equal(Buyer, audit.actor_user_id);
    }

    [Fact]
    public async Task A_dish_can_be_reported_and_shows_its_name_in_the_queue()
    {
        using var f = await Fixture.Create();

        var result = await f.Repository.CreateAsync(Draft("MENU_ITEM", 1), default);

        Assert.Equal(ContentReportOutcome.Created, result.Outcome);
        var queue = await new PlatformAdministrationRepository(f.Db, TimeProvider.System)
            .ListReportedContentAsync("PENDING", 1, 50, default);
        Assert.Equal("Bún bò", Assert.Single(queue.Items).ContentTitle);
    }

    [Fact]
    public async Task A_second_report_while_the_first_is_pending_is_a_duplicate_but_another_person_may_report()
    {
        using var f = await Fixture.Create();
        await f.Repository.CreateAsync(Draft("STOREFRONT", 1), default);

        Assert.Equal(ContentReportOutcome.Duplicate, (await f.Repository.CreateAsync(Draft("STOREFRONT", 1), default)).Outcome);
        Assert.Equal(ContentReportOutcome.Created, (await f.Repository.CreateAsync(Draft("STOREFRONT", 1, OtherBuyer), default)).Outcome);
        Assert.Equal(2, f.Db.ReportedContents.Count());
    }

    [Fact]
    public async Task Once_the_first_report_is_dismissed_the_same_person_may_report_again()
    {
        using var f = await Fixture.Create();
        var first = await f.Repository.CreateAsync(Draft("STOREFRONT", 1), default);
        var row = await f.Db.ReportedContents.SingleAsync(r => r.content_report_id == first.ReportId);
        row.status = ReportedContentStatuses.Dismissed;
        await f.Db.SaveChangesAsync();

        Assert.Equal(ContentReportOutcome.Created, (await f.Repository.CreateAsync(Draft("STOREFRONT", 1), default)).Outcome);
    }

    [Fact]
    public async Task Your_own_storefront_or_dish_cannot_be_reported()
    {
        using var f = await Fixture.Create();

        Assert.Equal(ContentReportOutcome.OwnContent, (await f.Repository.CreateAsync(Draft("STOREFRONT", 1, Seller), default)).Outcome);
        Assert.Equal(ContentReportOutcome.OwnContent, (await f.Repository.CreateAsync(Draft("MENU_ITEM", 1, Seller), default)).Outcome);
        Assert.Empty(f.Db.ReportedContents);
    }

    [Theory]
    [InlineData("STOREFRONT", 99)]
    [InlineData("MENU_ITEM", 99)]
    [InlineData("MENU_ITEM", 2)]      // HIDDEN dish
    [InlineData("MENU_ITEM", 3)]      // ARCHIVED dish
    [InlineData("REVIEW", 1)]         // not reportable through this path
    public async Task Missing_hidden_or_unsupported_content_is_not_found(string type, long id)
    {
        using var f = await Fixture.Create();

        var result = await f.Repository.CreateAsync(Draft(type, id), default);

        Assert.Equal(ContentReportOutcome.ContentNotFound, result.Outcome);
        Assert.Empty(f.Db.ReportedContents);
    }

    [Fact]
    public async Task A_storefront_a_moderator_has_hidden_cannot_be_reported_again()
    {
        using var f = await Fixture.Create();
        f.Db.ReportedContents.Add(new ReportedContent { content_type = "STOREFRONT", content_id = 1, reporter_user_id = OtherBuyer, reason = "x", status = "HIDDEN", reviewed_by = Admin, created_at = DateTime.UtcNow });
        await f.Db.SaveChangesAsync();

        var result = await f.Repository.CreateAsync(Draft("STOREFRONT", 1), default);

        Assert.Equal(ContentReportOutcome.ContentNotFound, result.Outcome);
    }

    [Fact]
    public async Task The_eleventh_report_within_an_hour_is_refused_with_the_time_to_wait()
    {
        var clock = new MutableClock(new DateTime(2026, 10, 5, 8, 0, 0, DateTimeKind.Utc));
        using var f = await Fixture.Create(clock);
        for (var id = 1; id <= 10; id++)
        {
            // Ten different dishes, one every minute.
            var dish = id == 1 ? 1 : 100 + id;
            if (dish != 1) await f.AddDish(dish);
            Assert.Equal(ContentReportOutcome.Created, (await f.Repository.CreateAsync(Draft("MENU_ITEM", dish), default)).Outcome);
            clock.Now = clock.Now.AddMinutes(1);
        }

        await f.AddDish(500);
        var refused = await f.Repository.CreateAsync(Draft("MENU_ITEM", 500), default);

        Assert.Equal(ContentReportOutcome.RateLimited, refused.Outcome);
        // The oldest report was at 08:00, so one more is allowed at 09:00; now it is 08:10.
        Assert.Equal(50 * 60, refused.RetryAfterSeconds);
        Assert.Equal(10, f.Db.ReportedContents.Count());

        clock.Now = new DateTime(2026, 10, 5, 9, 0, 1, DateTimeKind.Utc);
        Assert.Equal(ContentReportOutcome.Created, (await f.Repository.CreateAsync(Draft("MENU_ITEM", 500), default)).Outcome);
    }

    [Fact]
    public async Task The_limit_is_per_person()
    {
        var clock = new MutableClock(new DateTime(2026, 10, 5, 8, 0, 0, DateTimeKind.Utc));
        using var f = await Fixture.Create(clock);
        for (var id = 1; id <= 10; id++)
        {
            if (id != 1) await f.AddDish(100 + id);
            await f.Repository.CreateAsync(Draft("MENU_ITEM", id == 1 ? 1 : 100 + id), default);
        }

        var other = await f.Repository.CreateAsync(Draft("STOREFRONT", 1, OtherBuyer), default);

        Assert.Equal(ContentReportOutcome.Created, other.Outcome);
    }

    private sealed class MutableClock(DateTime start) : TimeProvider
    {
        public DateTime Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(Now, DateTimeKind.Utc));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        public TestContext Db { get; private set; } = null!;
        public ContentReportRepository Repository { get; private set; } = null!;

        public static async Task<Fixture> Create(TimeProvider? clock = null)
        {
            var f = new Fixture();
            await f.connection.OpenAsync();
            f.Db = new TestContext(new DbContextOptionsBuilder<StreetBizDbContext>().UseSqlite(f.connection).Options);
            await f.Db.Database.EnsureCreatedAsync();
            f.Repository = new ContentReportRepository(f.Db, clock ?? TimeProvider.System);

            f.Db.Roles.AddRange(
                new Role { role_code = "PLATFORM_ADMIN", role_name = "Admin" },
                new Role { role_code = "VENDOR", role_name = "Vendor" },
                new Role { role_code = "CUSTOMER", role_name = "Customer" });
            f.Db.AdministrativeUnits.Add(new AdministrativeUnit { unit_id = 1, unit_type = "WARD", unit_name = "Ward" });
            f.Db.UserAccounts.AddRange(
                User(Seller, "VENDOR", "Hộ bán"),
                User(Buyer, "CUSTOMER", "Khách A"),
                User(OtherBuyer, "CUSTOMER", "Khách B"),
                User(Admin, "PLATFORM_ADMIN", "Quản trị"));
            f.Db.Vendors.Add(new Vendor { vendor_id = 1, user_id = Seller });
            f.Db.PricingZones.Add(new PricingZone { zone_id = 1, ward_unit_id = 1, zone_name = "Zone", created_by = Admin });
            f.Db.BusinessRegistrations.Add(new BusinessRegistration { registration_id = 1, vendor_id = 1, ward_unit_id = 1, vendor_type = "ITINERANT", display_name = "Vendor", declared_address = "12 Nguyễn Văn Linh, Hải Châu, Đà Nẵng", registration_status = "APPROVED" });
            f.Db.SidewalkSlots.Add(new SidewalkSlot { slot_id = 1, zone_id = 1, slot_code = "SLOT", source = "WARD_DEFINED", slot_status = "ACTIVE" });
            f.Db.RentalApplications.Add(new RentalApplication { application_id = 1, registration_id = 1, slot_id = 1, application_method = "MANUAL_SELECTED", application_status = "APPROVED" });
            f.Db.RentalContracts.Add(new RentalContract { contract_id = 1, application_id = 1, slot_id = 1, vendor_id = 1, start_date = new DateOnly(2026, 1, 1), end_date = new DateOnly(2026, 12, 31), contract_status = "ACTIVE" });
            f.Db.Storefronts.Add(new Storefront { storefront_id = 1, registration_id = 1, contract_id = 1, storefront_name = "Quán Một", availability_status = "OPEN" });
            f.Db.FoodCategories.Add(new FoodCategory { category_id = 1, category_name = "Món nước" });
            f.Db.MenuItems.AddRange(
                Dish(1, "Bún bò", "AVAILABLE"),
                Dish(2, "Món bị ẩn", "HIDDEN"),
                Dish(3, "Món đã gỡ", "ARCHIVED"));
            await f.Db.SaveChangesAsync();
            f.Db.ChangeTracker.Clear();
            return f;
        }

        public async Task AddDish(long id)
        {
            Db.MenuItems.Add(Dish(id, $"Món {id}", "AVAILABLE"));
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        public void Dispose()
        {
            Db.Dispose();
            connection.Dispose();
        }

        private static MenuItem Dish(long id, string name, string status) => new()
        {
            menu_item_id = id,
            storefront_id = 1,
            category_id = 1,
            item_name = name,
            unit_price = 30_000,
            availability_status = status,
        };

        private static UserAccount User(long id, string role, string name) => new()
        {
            user_id = id,
            phone_number = $"090000000{id}",
            password_hash = "test-only",
            full_name = name,
            role_code = role,
            account_status = "ACTIVE",
            created_at = DateTime.UtcNow,
        };
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
                    if (property.ClrType == typeof(decimal) || property.ClrType == typeof(decimal?))
                        modelBuilder.Entity(entity.ClrType).Property(property.Name).HasConversion<double>();
                }
        }
    }
}
