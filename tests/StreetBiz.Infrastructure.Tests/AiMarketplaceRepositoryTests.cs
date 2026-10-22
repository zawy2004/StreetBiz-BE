using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Persistence.Repositories;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Tests;

/// <summary>What the AI features may read (AIP-02 prices, AIP-04 reviews, AIB-01 reports) and nothing more.</summary>
public sealed class AiMarketplaceRepositoryTests
{
    private static readonly DateTime Day = new(2026, 10, 10, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Categories_are_listed_by_name()
    {
        using var f = await Fixture.Create();

        var categories = await f.Repository.ListCategoriesAsync(default);

        Assert.Equal(["Drinks", "Mains"], categories.Select(c => c.CategoryName));
    }

    [Fact]
    public async Task The_reference_price_is_the_low_median_and_high_of_dishes_on_sale()
    {
        using var f = await Fixture.Create();

        var price = await f.Repository.GetCategoryPriceAsync(1, default);

        // 20k, 30k, 50k on sale in the open storefront; the hidden 1k dish and the paused storefront's 999k dish do not count.
        Assert.NotNull(price);
        Assert.Equal(3, price!.DishCount);
        Assert.Equal((20_000m, 30_000m, 50_000m), (price.Lowest, price.Median, price.Highest));
    }

    [Fact]
    public async Task Two_dishes_are_too_few_to_say_what_is_usual()
    {
        using var f = await Fixture.Create();

        Assert.Null(await f.Repository.GetCategoryPriceAsync(2, default));
    }

    [Fact]
    public async Task Review_text_is_read_for_a_public_storefront_without_hidden_or_empty_reviews()
    {
        using var f = await Fixture.Create();

        var row = await f.Repository.GetReviewsForSummaryAsync(1, 30, default);

        Assert.NotNull(row);
        Assert.Equal("Open Stall", row!.StorefrontName);
        // Five reviews have text; one was hidden by a moderator and one has no text.
        Assert.Equal(4, row.TotalReviews);
        Assert.DoesNotContain(row.Reviews, r => r.Text.Contains("hidden", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(row.Reviews.Select(r => r.CreatedAt).OrderByDescending(d => d), row.Reviews.Select(r => r.CreatedAt));
    }

    [Fact]
    public async Task The_number_of_reviews_sent_to_the_model_is_capped_but_the_total_is_not()
    {
        using var f = await Fixture.Create();

        var row = await f.Repository.GetReviewsForSummaryAsync(1, 2, default);

        Assert.Equal(2, row!.Reviews.Count);
        Assert.Equal(4, row.TotalReviews);
    }

    [Fact]
    public async Task A_storefront_that_is_not_public_has_no_reviews_to_summarise()
    {
        using var f = await Fixture.Create();

        Assert.Null(await f.Repository.GetReviewsForSummaryAsync(2, 30, default));
        Assert.Null(await f.Repository.GetReviewsForSummaryAsync(404, 30, default));
    }

    [Fact]
    public async Task A_report_carries_its_vendor_slot_and_the_scanned_permit_check_inputs()
    {
        using var f = await Fixture.Create();

        var report = await f.Repository.GetVendorReportAsync(1, default);

        Assert.NotNull(report);
        Assert.Equal("Open Stall", report!.VendorName);
        Assert.Equal("S-01", report.SlotCode);
        Assert.Equal(1, report.ScannedPermitId);
        Assert.Equal("ACTIVE", report.PermitStatus);
        Assert.Equal("ACTIVE", report.PermitContractStatus);
        Assert.Equal(1, report.PermitSlotId);
        Assert.Equal([1], report.WardIds);
        Assert.Null(await f.Repository.GetVendorReportAsync(404, default));
    }

    [Fact]
    public async Task A_ward_sees_reports_by_slot_scanned_permit_or_registered_vendor_and_no_others()
    {
        using var f = await Fixture.Create();

        var ward1 = await f.Repository.ListVendorReportsForWardAsync(1, null, 1, 20, default);
        var ward2 = await f.Repository.ListVendorReportsForWardAsync(2, null, 1, 20, default);

        // Report 1 names a ward-1 slot, report 3 only scanned a ward-1 permit, report 2 is about a ward-2 vendor.
        Assert.Equal([3L, 1L], ward1.Select(r => r.ReportId));
        Assert.Equal([2L], ward2.Select(r => r.ReportId));
    }

    [Fact]
    public async Task The_list_can_be_filtered_by_status_and_paged()
    {
        using var f = await Fixture.Create();

        Assert.Equal([3L], (await f.Repository.ListVendorReportsForWardAsync(1, "REVIEWED", 1, 20, default)).Select(r => r.ReportId));
        Assert.Equal([3L], (await f.Repository.ListVendorReportsForWardAsync(1, null, 1, 1, default)).Select(r => r.ReportId));
        Assert.Equal([1L], (await f.Repository.ListVendorReportsForWardAsync(1, null, 2, 1, default)).Select(r => r.ReportId));
        Assert.Empty(await f.Repository.ListVendorReportsForWardAsync(1, null, 3, 1, default));
    }

    [Fact]
    public async Task A_ward_may_open_a_report_photo_only_when_the_report_is_in_that_ward()
    {
        using var f = await Fixture.Create();

        Assert.True(await f.Repository.ReportEvidenceBelongsToWardAsync("/api/uploads/evidence/50/photo-1.jpg", 1, default));
        Assert.False(await f.Repository.ReportEvidenceBelongsToWardAsync("/api/uploads/evidence/50/photo-1.jpg", 2, default));
        Assert.True(await f.Repository.ReportEvidenceBelongsToWardAsync("/api/uploads/evidence/50/photo-2.jpg", 2, default));
        Assert.False(await f.Repository.ReportEvidenceBelongsToWardAsync("/api/uploads/evidence/50/unknown.jpg", 1, default));
    }

    [Fact]
    public async Task The_extracted_location_is_stored_on_the_report_and_capped_at_its_column_length()
    {
        using var f = await Fixture.Create();

        await f.Repository.SetReportLocationAsync(1, new string('x', 400), default);
        await f.Repository.SetReportLocationAsync(404, "nowhere", default);

        var report = await f.Repository.GetVendorReportAsync(1, default);
        Assert.Equal(200, report!.AiExtractedLocation!.Length);
        // Only the note changed: the report is still waiting for the officer.
        Assert.Equal("PENDING", report.Status);
    }

    private sealed class Fixture : IDisposable
    {
        private SqliteConnection Connection { get; } = new("Data Source=:memory:");
        public TestContext Db { get; private set; } = null!;
        public AiMarketplaceRepository Repository { get; private set; } = null!;

        public static async Task<Fixture> Create()
        {
            var f = new Fixture();
            await f.Connection.OpenAsync();
            f.Db = new TestContext(new DbContextOptionsBuilder<StreetBizDbContext>().UseSqlite(f.Connection).Options);
            await f.Db.Database.EnsureCreatedAsync();
            await f.Db.Database.ExecuteSqlRawAsync(
                "CREATE VIEW vw_VendorRatings AS SELECT vendor_id, 0 AS verified_count, NULL AS verified_rating, " +
                "0 AS community_count, NULL AS community_rating FROM Vendors");
            f.Repository = new AiMarketplaceRepository(f.Db, new CommerceRepository(f.Db, TimeProvider.System));

            f.Db.Roles.AddRange(
                new Role { role_code = "PLATFORM_ADMIN", role_name = "Admin" },
                new Role { role_code = "VENDOR", role_name = "Vendor" },
                new Role { role_code = "CUSTOMER", role_name = "Customer" });
            f.Db.AdministrativeUnits.AddRange(
                new AdministrativeUnit { unit_id = 10, unit_type = "DISTRICT", unit_name = "District" },
                new AdministrativeUnit { unit_id = 1, unit_type = "WARD", unit_name = "One", parent_unit_id = 10 },
                new AdministrativeUnit { unit_id = 2, unit_type = "WARD", unit_name = "Two", parent_unit_id = 10 });
            f.Db.UserAccounts.AddRange(Account(1, "PLATFORM_ADMIN"), Account(50, "CUSTOMER"));
            f.Db.FoodCategories.AddRange(
                new FoodCategory { category_id = 1, category_name = "Mains", created_by = 1 },
                new FoodCategory { category_id = 2, category_name = "Drinks", created_by = 1 });
            foreach (var ward in new[] { 1, 2 })
                f.Db.PricingZones.Add(new PricingZone
                {
                    zone_id = ward, ward_unit_id = ward, ward_unit_type = "WARD", zone_name = $"Zone {ward}",
                    zone_code = $"Z-{ward}", price_per_day = 25000, created_by = 1,
                });

            f.AddStorefront(1, "Open Stall", ward: 1, availability: "OPEN");
            f.AddStorefront(2, "Paused Stall", ward: 2, availability: "PAUSED");

            f.Db.MenuItems.AddRange(
                Item(10, 1, 1, "Dish A", 20_000, "AVAILABLE"),
                Item(11, 1, 1, "Dish B", 30_000, "AVAILABLE"),
                Item(12, 1, 1, "Dish C", 50_000, "SOLD_OUT"),
                Item(13, 1, 1, "Dish hidden", 1_000, "HIDDEN"),
                Item(14, 2, 1, "Dish paused", 999_000, "AVAILABLE"),
                Item(20, 1, 2, "Tea", 10_000, "AVAILABLE"),
                Item(21, 1, 2, "Coffee", 20_000, "AVAILABLE"));

            // Six reviews of the open storefront: four readable, one empty, one hidden by a moderator.
            var texts = new string?[] { "Ngon", "Hơi chậm", "Giá hợp lý", "Sạch sẽ", null, "Hidden bad words" };
            for (var i = 0; i < texts.Length; i++)
            {
                var id = i + 1;
                f.Db.Orders.Add(new Order
                {
                    order_id = id, order_code = $"O-{id}", customer_user_id = 50, storefront_id = 1,
                    order_status = "COMPLETED", subtotal_amount = 10_000, total_amount = 10_000, created_at = Day,
                });
                f.Db.Reviews.Add(new Review
                {
                    review_id = id, order_id = id, customer_user_id = 50, rating = 5, review_text = texts[i],
                    created_at = Day.AddHours(id),
                });
            }

            f.Db.ReportedContents.Add(new ReportedContent
            {
                content_type = "REVIEW", content_id = 6, reporter_user_id = 50, reason = "abuse", status = "HIDDEN", created_at = Day,
            });

            f.Db.DigitalPermits.Add(new DigitalPermit
            {
                permit_id = 1, contract_id = 1, qr_payload = "qr-1", permit_status = "ACTIVE", issued_at = Day,
            });
            // 1: names a ward-1 slot and scanned its permit. 2: only names a ward-2 vendor. 3: only a scanned ward-1 permit.
            f.Db.VendorReports.AddRange(
                Report(1, vendorId: 101, slotId: 1, permitId: 1, status: "PENDING", created: Day),
                Report(2, vendorId: 102, slotId: null, permitId: null, status: "PENDING", created: Day.AddHours(1)),
                Report(3, vendorId: null, slotId: null, permitId: 1, status: "REVIEWED", created: Day.AddHours(2)));
            await f.Db.SaveChangesAsync();
            return f;
        }

        private static VendorReport Report(long id, long? vendorId, long? slotId, long? permitId, string status, DateTime created) => new()
        {
            report_id = id, reporter_user_id = 50, vendor_id = vendorId, slot_id = slotId, scanned_permit_id = permitId,
            report_reason = "Bán lấn vỉa hè", report_status = status, created_at = created,
            evidence_url = id < 3 ? $"/api/uploads/evidence/50/photo-{id}.jpg" : null,
        };

        private static UserAccount Account(long id, string role, string status = "ACTIVE") => new()
        {
            user_id = id, phone_number = $"09000{id:000}", password_hash = "test-only", full_name = $"User {id}",
            role_code = role, ward_unit_id = 1, account_status = status,
        };

        private static MenuItem Item(long id, long storefrontId, int categoryId, string name, decimal price, string status) => new()
        {
            menu_item_id = id, storefront_id = storefrontId, category_id = categoryId, item_name = name,
            unit_price = price, availability_status = status,
        };

        private void AddStorefront(long id, string name, int ward, string availability)
        {
            var userId = 100 + id;
            Db.UserAccounts.Add(Account(userId, "VENDOR"));
            Db.Vendors.Add(new Vendor { vendor_id = userId, user_id = userId });
            Db.BusinessRegistrations.Add(new BusinessRegistration
            {
                registration_id = id, vendor_id = userId, ward_unit_id = ward, vendor_type = "FIXED_STOREFRONT",
                display_name = name, registration_status = "APPROVED",
            });
            Db.SidewalkSlots.Add(new SidewalkSlot
            {
                slot_id = id, zone_id = ward, slot_code = $"S-{id:00}", latitude = 16.06m, longitude = 108.21m,
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
                availability_status = availability,
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
