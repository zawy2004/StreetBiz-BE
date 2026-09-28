using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Moq;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.FoodSafety;
using StreetBiz.Application.Features.WardSlots;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;
using StreetBiz.Infrastructure.Services;

namespace StreetBiz.Infrastructure.Tests;

public sealed class FoodSafetyServiceTests
{
    private const string Evidence = "/api/uploads/evidence/1/0123456789abcdef0123456789abcdef.pdf";
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7));

    private static FoodSafetySubmitInput Input(params long[] items) =>
        new(1, items, "Bếp sạch", [new FoodSafetyEvidenceInput("CERTIFICATE", Evidence)]);

    private static FoodSafetyDecisionInput Decision(string decision, string expected, string? department = null,
        string? number = null, DateOnly? issued = null, DateOnly? expires = null) =>
        new(decision, "Ghi chú", expected, department, number, issued, expires);

    [Fact]
    public async Task Full_workflow_submit_forward_and_record_approval()
    {
        using var f = await Fixture.Create();
        var submitted = await f.Service.Submit(Input(1), default);
        Assert.Equal(FoodSafetyStatuses.Submitted, submitted.Status);
        Assert.Equal(["WITHDRAW"], submitted.Actions);

        var queue = await f.Service.WardList(null, default);
        Assert.Equal(submitted.ApplicationId, Assert.Single(queue).ApplicationId);
        Assert.Contains(FoodSafetyDecisions.Forward, queue[0].Actions);

        // Forwarding needs the department's name.
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            f.Service.Decide(submitted.ApplicationId, Decision(FoodSafetyDecisions.Forward, "SUBMITTED"), default));
        var forwarded = await f.Service.Decide(submitted.ApplicationId,
            Decision(FoodSafetyDecisions.Forward, "SUBMITTED", department: "Chi cục ATTP Đà Nẵng"), default);
        Assert.Equal(FoodSafetyStatuses.Forwarded, forwarded.Status);
        Assert.NotNull(forwarded.ForwardedAt);

        // A stale screen cannot act on the old status.
        await Assert.ThrowsAsync<ConflictException>(() => f.Service.Decide(submitted.ApplicationId,
            Decision(FoodSafetyDecisions.Reject, "SUBMITTED"), default));
        // The certificate must still be in date.
        await Assert.ThrowsAsync<DomainRuleException>(() => f.Service.Decide(submitted.ApplicationId,
            Decision(FoodSafetyDecisions.RecordApproved, "FORWARDED", number: "ATTP-1",
                issued: Today.AddYears(-3), expires: Today.AddDays(-1)), default));

        var approved = await f.Service.Decide(submitted.ApplicationId,
            Decision(FoodSafetyDecisions.RecordApproved, "FORWARDED", number: "ATTP-1",
                issued: Today.AddDays(-5), expires: Today.AddYears(3)), default);
        Assert.Equal(FoodSafetyStatuses.Approved, approved.Status);
        Assert.Equal("ATTP-1", approved.CertificateNumber);
        Assert.Empty(approved.Actions);
        Assert.Equal(2, await f.Db.Notifications.CountAsync());
        Assert.Equal(2, await f.Db.AuditLogs.CountAsync());
        Assert.True(await f.Service.EvidenceBelongsToWardAsync(Evidence, 1, default));
        Assert.False(await f.Service.EvidenceBelongsToWardAsync(Evidence, 2, default));
    }

    [Fact]
    public async Task Request_info_then_resubmit_returns_to_the_ward_queue()
    {
        using var f = await Fixture.Create();
        var submitted = await f.Service.Submit(Input(1), default);
        await f.Service.Decide(submitted.ApplicationId, Decision(FoodSafetyDecisions.RequestInfo, "SUBMITTED"), default);
        var mine = await f.Service.VendorGet(submitted.ApplicationId, default);
        Assert.Equal(FoodSafetyStatuses.MoreInformationRequired, mine.Status);
        Assert.Contains("RESUBMIT", mine.Actions);

        var again = await f.Service.Resubmit(submitted.ApplicationId, Input(1, 2), default);
        Assert.Equal(FoodSafetyStatuses.Submitted, again.Status);
        Assert.Equal(2, again.Dishes.Count);
        Assert.Null(again.ReviewReason);
    }

    [Fact]
    public async Task A_dish_cannot_be_claimed_twice_or_come_from_another_stall()
    {
        using var f = await Fixture.Create();
        await f.Service.Submit(Input(1), default);
        await Assert.ThrowsAsync<ConflictException>(() => f.Service.Submit(Input(1), default));
        await Assert.ThrowsAsync<DomainRuleException>(() => f.Service.Submit(Input(99), default));
        await Assert.ThrowsAsync<DomainRuleException>(() => f.Service.Submit(
            new FoodSafetySubmitInput(1, [2], null, []), default));
        // Evidence must be the vendor's own upload.
        await Assert.ThrowsAsync<DomainRuleException>(() => f.Service.Submit(new FoodSafetySubmitInput(1, [2], null,
            [new FoodSafetyEvidenceInput("CERTIFICATE", "/api/uploads/evidence/7/0123456789abcdef0123456789abcdef.pdf")]), default));
    }

    [Fact]
    public async Task Withdraw_only_before_forwarding_and_other_wards_see_nothing()
    {
        using var f = await Fixture.Create();
        var submitted = await f.Service.Submit(Input(1), default);
        await f.Service.Decide(submitted.ApplicationId,
            Decision(FoodSafetyDecisions.Forward, "SUBMITTED", department: "Chi cục ATTP"), default);
        await Assert.ThrowsAsync<ConflictException>(() => f.Service.Withdraw(submitted.ApplicationId, default));

        f.Ward.Setup(x => x.RequireAsync(default)).ReturnsAsync(new WardActor(3, 2, "Other ward"));
        Assert.Empty(await f.Service.WardList(null, default));
        await Assert.ThrowsAsync<NotFoundException>(() => f.Service.WardGet(submitted.ApplicationId, default));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        public TestContext Db { get; private set; } = null!;
        public Mock<IVendorContext> Vendor { get; } = new();
        public Mock<IWardActorContext> Ward { get; } = new();
        public Mock<IFileStorage> Storage { get; } = new();
        public FoodSafetyService Service { get; private set; } = null!;

        public static async Task<Fixture> Create()
        {
            var f = new Fixture();
            await f.connection.OpenAsync();
            f.Db = new TestContext(new DbContextOptionsBuilder<StreetBizDbContext>().UseSqlite(f.connection).Options);
            await f.Db.Database.EnsureCreatedAsync();
            f.Vendor.Setup(x => x.RequireVendorIdAsync(default)).ReturnsAsync(1);
            f.Ward.Setup(x => x.RequireAsync(default)).ReturnsAsync(new WardActor(3, 1, "Officer"));
            f.Storage.Setup(x => x.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
            f.Service = new(f.Db, f.Vendor.Object, f.Ward.Object, f.Storage.Object, TimeProvider.System);
            f.Db.Roles.AddRange(new Role { role_code = "VENDOR", role_name = "Vendor" },
                new Role { role_code = "WARD_AUTHORITY", role_name = "Ward" });
            f.Db.AdministrativeUnits.AddRange(new AdministrativeUnit { unit_id = 1, unit_type = "WARD", unit_name = "Ward 1" },
                new AdministrativeUnit { unit_id = 2, unit_type = "WARD", unit_name = "Ward 2" });
            f.Db.UserAccounts.Add(new UserAccount { user_id = 1, phone_number = "0900000001", password_hash = "test-only", full_name = "Vendor", role_code = "VENDOR", account_status = "ACTIVE" });
            f.Db.UserAccounts.Add(new UserAccount { user_id = 3, phone_number = "0900000003", password_hash = "test-only", full_name = "Officer", role_code = "WARD_AUTHORITY", account_status = "ACTIVE", ward_unit_id = 1 });
            f.Db.Vendors.Add(new Vendor { vendor_id = 1, user_id = 1 });
            f.Db.PricingZones.Add(new PricingZone { zone_id = 1, ward_unit_id = 1, zone_name = "Zone", created_by = 3 });
            f.Db.BusinessRegistrations.Add(new BusinessRegistration { registration_id = 1, vendor_id = 1, ward_unit_id = 1, vendor_type = "ITINERANT", display_name = "Vendor", registration_status = "APPROVED" });
            f.Db.SidewalkSlots.Add(new SidewalkSlot { slot_id = 1, zone_id = 1, slot_code = "SLOT", source = "WARD_DEFINED", slot_status = "ACTIVE" });
            f.Db.RentalApplications.Add(new RentalApplication { application_id = 1, registration_id = 1, slot_id = 1, application_method = "MANUAL_SELECTED", application_status = "APPROVED" });
            f.Db.RentalContracts.Add(new RentalContract { contract_id = 1, application_id = 1, slot_id = 1, vendor_id = 1, start_date = Today.AddDays(-1), end_date = Today.AddDays(30), contract_status = "ACTIVE" });
            f.Db.Storefronts.Add(new Storefront { storefront_id = 1, registration_id = 1, contract_id = 1, storefront_name = "Store", availability_status = "OPEN" });
            f.Db.FoodCategories.Add(new FoodCategory { category_id = 1, category_name = "Món nước", requires_food_safety = true });
            f.Db.MenuItems.AddRange(
                new MenuItem { menu_item_id = 1, storefront_id = 1, category_id = 1, item_name = "Phở", unit_price = 40000, availability_status = "AVAILABLE" },
                new MenuItem { menu_item_id = 2, storefront_id = 1, category_id = 1, item_name = "Bún bò", unit_price = 45000, availability_status = "AVAILABLE" });
            await f.Db.SaveChangesAsync();
            f.Db.ChangeTracker.Clear();
            return f;
        }

        public void Dispose() { Db.Dispose(); connection.Dispose(); }
    }

    private sealed class TestContext(DbContextOptions<StreetBizDbContext> options) : StreetBizDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            foreach (var entity in modelBuilder.Model.GetEntityTypes())
                foreach (var property in entity.GetProperties())
                {
                    if (property.GetComputedColumnSql() is not null) { property.SetComputedColumnSql(null); property.ValueGenerated = ValueGenerated.Never; }
                    property.SetDefaultValueSql(null);
                    if (property.ClrType == typeof(decimal) || property.ClrType == typeof(decimal?))
                        modelBuilder.Entity(entity.ClrType).Property(property.Name).HasConversion<double>();
                }
        }
    }
}
