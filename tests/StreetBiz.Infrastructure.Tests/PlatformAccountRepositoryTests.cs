using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Persistence.Repositories;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Tests;

/// <summary>ADM-02 against a real (SQLite) schema: the transaction, session revocation and storefront pause.</summary>
public sealed class PlatformAccountRepositoryTests
{
    private const long Admin = 1;
    private const long VendorUser = 2;
    private const long CustomerUser = 3;
    private const long Officer = 4;

    [Fact]
    public async Task The_list_never_includes_officers_or_administrators()
    {
        using var f = await Fixture.Create();

        var page = await f.Repository.ListAccountsAsync(null, null, null, 1, 50, default);

        Assert.Equal(new[] { VendorUser, CustomerUser }, page.Items.Select(row => row.UserId).Order().ToArray());
        Assert.Equal(2, page.TotalCount);
    }

    [Fact]
    public async Task The_list_filters_by_role_status_and_search_text()
    {
        using var f = await Fixture.Create();

        var vendors = await f.Repository.ListAccountsAsync(RoleCodes.Vendor, null, null, 1, 50, default);
        var byName = await f.Repository.ListAccountsAsync(null, null, "bán", 1, 50, default);
        var byPhone = await f.Repository.ListAccountsAsync(null, null, "0900000003", 1, 50, default);
        var suspended = await f.Repository.ListAccountsAsync(null, AccountStatuses.Suspended, null, 1, 50, default);
        var wildcard = await f.Repository.ListAccountsAsync(null, null, "%", 1, 50, default);

        Assert.Equal([VendorUser], vendors.Items.Select(row => row.UserId));
        Assert.Equal([VendorUser], byName.Items.Select(row => row.UserId));
        Assert.Equal([CustomerUser], byPhone.Items.Select(row => row.UserId));
        Assert.Empty(suspended.Items);
        Assert.Empty(wildcard.Items);
    }

    [Fact]
    public async Task Suspending_revokes_sessions_pauses_open_storefronts_and_leaves_a_trail()
    {
        using var f = await Fixture.Create();

        var result = await f.Repository.ChangeAccountStatusAsync(
            VendorUser, AccountStatuses.Suspended, AccountStatuses.Active, "Bán hàng giả", Admin, default);

        Assert.Equal(AccountStatusChangeOutcome.Updated, result.Outcome);
        Assert.Equal(AccountStatuses.Suspended, result.Account!.Account.AccountStatus);
        Assert.Equal(0, result.Account.Account.ActiveSessions);
        var history = Assert.Single(result.Account.History);
        Assert.Equal(AccountAuditActions.Suspend, history.Action);
        Assert.Equal("Bán hàng giả", history.Reason);
        Assert.Equal("Quản trị", history.ActorName);

        f.Db.ChangeTracker.Clear();
        Assert.All(f.Db.UserSessions.Where(s => s.user_id == VendorUser), s => Assert.NotNull(s.revoked_at));
        Assert.Equal("PAUSED", f.Db.Storefronts.Single().availability_status);
        var notification = f.Db.Notifications.Single(n => n.user_id == VendorUser);
        Assert.Contains("Bán hàng giả", notification.body);
        Assert.Single(f.Db.AuditLogs, a => a.action == AccountAuditActions.Suspend && a.entity_id == VendorUser);
    }

    [Fact]
    public async Task Another_users_sessions_are_left_alone()
    {
        using var f = await Fixture.Create();

        await f.Repository.ChangeAccountStatusAsync(
            VendorUser, AccountStatuses.Suspended, AccountStatuses.Active, "Vi phạm", Admin, default);

        f.Db.ChangeTracker.Clear();
        Assert.All(f.Db.UserSessions.Where(s => s.user_id == CustomerUser), s => Assert.Null(s.revoked_at));
    }

    [Fact]
    public async Task Reactivating_does_not_reopen_the_storefront()
    {
        using var f = await Fixture.Create();
        await f.Repository.ChangeAccountStatusAsync(
            VendorUser, AccountStatuses.Suspended, AccountStatuses.Active, "Vi phạm", Admin, default);

        var result = await f.Repository.ChangeAccountStatusAsync(
            VendorUser, AccountStatuses.Active, AccountStatuses.Suspended, "Đã làm rõ", Admin, default);

        Assert.Equal(AccountStatusChangeOutcome.Updated, result.Outcome);
        Assert.Equal(AccountStatuses.Active, result.Account!.Account.AccountStatus);
        Assert.Equal(
            [AccountAuditActions.Reactivate, AccountAuditActions.Suspend],
            result.Account.History.Select(e => e.Action));
        f.Db.ChangeTracker.Clear();
        Assert.Equal("PAUSED", f.Db.Storefronts.Single().availability_status);
    }

    [Fact]
    public async Task A_stale_expected_status_is_a_conflict_and_changes_nothing()
    {
        using var f = await Fixture.Create();

        var result = await f.Repository.ChangeAccountStatusAsync(
            VendorUser, AccountStatuses.Active, AccountStatuses.Suspended, null, Admin, default);

        Assert.Equal(AccountStatusChangeOutcome.Conflict, result.Outcome);
        f.Db.ChangeTracker.Clear();
        Assert.Empty(f.Db.AuditLogs);
        Assert.Empty(f.Db.Notifications);
    }

    [Fact]
    public async Task Suspending_twice_is_a_conflict()
    {
        using var f = await Fixture.Create();
        await f.Repository.ChangeAccountStatusAsync(
            VendorUser, AccountStatuses.Suspended, AccountStatuses.Active, "Vi phạm", Admin, default);

        var again = await f.Repository.ChangeAccountStatusAsync(
            VendorUser, AccountStatuses.Suspended, AccountStatuses.Active, "Vi phạm", Admin, default);

        Assert.Equal(AccountStatusChangeOutcome.Conflict, again.Outcome);
    }

    [Theory]
    [InlineData(Admin)]
    [InlineData(Officer)]
    public async Task Officers_and_administrators_cannot_be_suspended(long userId)
    {
        using var f = await Fixture.Create();

        var result = await f.Repository.ChangeAccountStatusAsync(
            userId, AccountStatuses.Suspended, AccountStatuses.Active, "Vi phạm", Admin, default);

        Assert.Equal(AccountStatusChangeOutcome.RoleNotManageable, result.Outcome);
        f.Db.ChangeTracker.Clear();
        Assert.All(f.Db.UserAccounts, user => Assert.Equal("ACTIVE", user.account_status));
    }

    [Fact]
    public async Task An_unknown_account_is_not_found_and_an_officer_detail_is_hidden()
    {
        using var f = await Fixture.Create();

        Assert.Equal(AccountStatusChangeOutcome.NotFound, (await f.Repository.ChangeAccountStatusAsync(
            999, AccountStatuses.Suspended, AccountStatuses.Active, "Vi phạm", Admin, default)).Outcome);
        Assert.Null(await f.Repository.GetAccountAsync(Officer, default));
        Assert.NotNull(await f.Repository.GetAccountAsync(VendorUser, default));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        public TestContext Db { get; private set; } = null!;
        public PlatformAdministrationRepository Repository { get; private set; } = null!;

        public static async Task<Fixture> Create()
        {
            var f = new Fixture();
            await f.connection.OpenAsync();
            f.Db = new TestContext(new DbContextOptionsBuilder<StreetBizDbContext>().UseSqlite(f.connection).Options);
            await f.Db.Database.EnsureCreatedAsync();
            f.Repository = new PlatformAdministrationRepository(f.Db, TimeProvider.System);
            f.Db.Roles.AddRange(
                new Role { role_code = "PLATFORM_ADMIN", role_name = "Admin" },
                new Role { role_code = "VENDOR", role_name = "Vendor" },
                new Role { role_code = "CUSTOMER", role_name = "Customer" },
                new Role { role_code = "WARD_AUTHORITY", role_name = "Ward" });
            f.Db.AdministrativeUnits.Add(new AdministrativeUnit { unit_id = 1, unit_type = "WARD", unit_name = "Ward" });
            f.Db.UserAccounts.AddRange(
                User(Admin, "PLATFORM_ADMIN", "Quản trị"),
                User(VendorUser, "VENDOR", "Hộ bán hàng"),
                User(CustomerUser, "CUSTOMER", "Khách mua"),
                User(Officer, "WARD_AUTHORITY", "Cán bộ"));
            f.Db.Vendors.Add(new Vendor { vendor_id = 1, user_id = VendorUser });
            f.Db.PricingZones.Add(new PricingZone { zone_id = 1, ward_unit_id = 1, zone_name = "Zone", created_by = Admin });
            f.Db.BusinessRegistrations.Add(new BusinessRegistration { registration_id = 1, vendor_id = 1, ward_unit_id = 1, vendor_type = "ITINERANT", display_name = "Vendor", declared_address = "12 Nguyễn Văn Linh, Hải Châu, Đà Nẵng", registration_status = "APPROVED" });
            f.Db.SidewalkSlots.Add(new SidewalkSlot { slot_id = 1, zone_id = 1, slot_code = "SLOT", source = "WARD_DEFINED", slot_status = "ACTIVE" });
            f.Db.RentalApplications.Add(new RentalApplication { application_id = 1, registration_id = 1, slot_id = 1, application_method = "MANUAL_SELECTED", application_status = "APPROVED" });
            f.Db.RentalContracts.Add(new RentalContract { contract_id = 1, application_id = 1, slot_id = 1, vendor_id = 1, start_date = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)), end_date = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)), contract_status = "ACTIVE" });
            f.Db.Storefronts.Add(new Storefront { storefront_id = 1, registration_id = 1, contract_id = 1, storefront_name = "Store", availability_status = "OPEN" });
            f.Db.UserSessions.AddRange(
                Session(VendorUser, 1),
                Session(VendorUser, 2),
                Session(CustomerUser, 3));
            await f.Db.SaveChangesAsync();
            f.Db.ChangeTracker.Clear();
            return f;
        }

        public void Dispose()
        {
            Db.Dispose();
            connection.Dispose();
        }

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

        private static UserSession Session(long userId, byte marker) => new()
        {
            user_id = userId,
            refresh_token_hash = [marker],
            expires_at = DateTime.UtcNow.AddDays(10),
            created_at = DateTime.UtcNow.AddMinutes(-marker),
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
