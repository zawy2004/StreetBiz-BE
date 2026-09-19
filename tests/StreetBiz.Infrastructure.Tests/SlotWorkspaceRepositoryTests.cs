using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Moq;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Persistence.Repositories;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Tests;

/// <summary>The slot workspace reads (tenant name, live hold) and the slot-hold table's single-row-per-slot rule.</summary>
public sealed class SlotWorkspaceRepositoryTests
{
    private static readonly DateTime Now = new(2026, 9, 19, 8, 0, 0, DateTimeKind.Utc);
    private static readonly SlotSearchArea WholeZone = new(null, null, null, null, null, 1, true);

    [Fact]
    public async Task A_slot_row_shows_the_tenant_only_while_the_contract_is_active()
    {
        using var f = await Fixture.Create();

        var rows = await f.Slots.SearchAsync(WholeZone, default);

        Assert.Equal("Vendor 1", rows.Single(r => r.SlotId == 2).TenantName);
        Assert.Null(rows.Single(r => r.SlotId == 1).TenantName);

        (await f.Db.RentalContracts.FindAsync(1L))!.contract_status = "EXPIRED";
        await f.Db.SaveChangesAsync();

        Assert.Null((await f.Slots.GetByIdAsync(2, default))!.TenantName);
    }

    [Fact]
    public async Task A_slot_row_reports_the_hold_expiry_only_while_it_is_live()
    {
        using var f = await Fixture.Create();
        await f.Holds.UpsertAsync(1, 1, Now, Now.AddMinutes(15), default);

        Assert.Equal(Now.AddMinutes(15), (await f.Slots.GetByIdAsync(1, default))!.HoldExpiresAt);

        f.Clock.Setup(c => c.UtcNow).Returns(Now.AddMinutes(16));
        Assert.Null((await f.Slots.GetByIdAsync(1, default))!.HoldExpiresAt);
    }

    [Fact]
    public async Task The_owner_can_renew_a_hold_and_the_expiry_moves()
    {
        using var f = await Fixture.Create();
        await f.Holds.UpsertAsync(1, 1, Now, Now.AddMinutes(15), default);

        await f.Holds.UpsertAsync(1, 1, Now.AddMinutes(10), Now.AddMinutes(25), default);

        var hold = await f.Holds.GetActiveBySlotAsync(1, Now.AddMinutes(11), default);
        Assert.Equal(Now.AddMinutes(25), hold!.ExpiresAt);
        Assert.Equal(1, await f.Db.SlotHolds.CountAsync());
    }

    [Fact]
    public async Task A_live_hold_of_another_registration_cannot_be_taken_over()
    {
        using var f = await Fixture.Create();
        await f.Holds.UpsertAsync(1, 1, Now, Now.AddMinutes(15), default);

        // A second request runs on its own DbContext. SQLite raises a plain DbUpdateException for
        // the duplicate key; on SQL Server the same violation is translated to a ConflictException
        // by SqlErrorTranslator (see its tests).
        await using var otherRequest = f.NewContext();
        await Assert.ThrowsAsync<DbUpdateException>(() =>
            new SlotHoldRepository(otherRequest).UpsertAsync(1, 2, Now.AddMinutes(1), Now.AddMinutes(16), default));

        Assert.Equal(1, (await f.Db.SlotHolds.AsNoTracking().SingleAsync()).registration_id);
    }

    [Fact]
    public async Task An_expired_hold_can_be_taken_over()
    {
        using var f = await Fixture.Create();
        await f.Holds.UpsertAsync(1, 1, Now, Now.AddMinutes(15), default);

        await f.Holds.UpsertAsync(1, 2, Now.AddMinutes(20), Now.AddMinutes(35), default);

        var hold = await f.Db.SlotHolds.AsNoTracking().SingleAsync();
        Assert.Equal(2, hold.registration_id);
        Assert.Equal(Now.AddMinutes(35), hold.expires_at);
    }

    [Fact]
    public async Task Only_the_owner_can_release_a_hold_and_only_live_holds_are_listed()
    {
        using var f = await Fixture.Create();
        await f.Holds.UpsertAsync(1, 1, Now, Now.AddMinutes(15), default);

        Assert.False(await f.Holds.ReleaseAsync(1, 2, default));
        Assert.Single(await f.Holds.ListActiveByRegistrationAsync(1, Now.AddMinutes(5), default));
        Assert.Empty(await f.Holds.ListActiveByRegistrationAsync(1, Now.AddMinutes(16), default));

        Assert.True(await f.Holds.ReleaseAsync(1, 1, default));
        Assert.Empty(await f.Holds.ListActiveByRegistrationAsync(1, Now.AddMinutes(5), default));
    }

    [Fact]
    public async Task A_zone_detail_carries_the_ward_contact_fee_table_and_features_in_order()
    {
        using var f = await Fixture.Create();

        var zone = await f.Zones.GetDetailAsync(1, default);

        Assert.NotNull(zone);
        Assert.Equal("KZ-01", zone!.ZoneCode);
        Assert.Equal("Ward Office", zone.ContactName);
        Assert.Equal(["Cleaning", "Deposit"], zone.FeeComponents.Select(c => c.ComponentName));
        Assert.Equal("TRANSFORMER", Assert.Single(zone.Features).FeatureType);
        Assert.Null(await f.Zones.GetDetailAsync(99, default));
    }

    private sealed class Fixture : IDisposable
    {
        private SqliteConnection Connection { get; } = new("Data Source=:memory:");
        public TestContext Db { get; private set; } = null!;
        public Mock<IDateTimeProvider> Clock { get; } = new();
        public SidewalkSlotRepository Slots { get; private set; } = null!;
        public SlotHoldRepository Holds { get; private set; } = null!;
        public SidewalkZoneRepository Zones { get; private set; } = null!;

        /// <summary>Another context on the same database, like a concurrent HTTP request would get.</summary>
        public StreetBizDbContext NewContext() =>
            new TestContext(new DbContextOptionsBuilder<StreetBizDbContext>().UseSqlite(Connection).Options);

        public static async Task<Fixture> Create()
        {
            var f = new Fixture();
            await f.Connection.OpenAsync();
            f.Db = new TestContext(new DbContextOptionsBuilder<StreetBizDbContext>().UseSqlite(f.Connection).Options);
            await f.Db.Database.EnsureCreatedAsync();
            f.Clock.Setup(c => c.UtcNow).Returns(Now);
            f.Slots = new SidewalkSlotRepository(f.Db, f.Clock.Object);
            f.Holds = new SlotHoldRepository(f.Db);
            f.Zones = new SidewalkZoneRepository(f.Db);

            f.Db.Roles.AddRange(new Role { role_code = "WARD_AUTHORITY", role_name = "Ward" },
                new Role { role_code = "VENDOR", role_name = "Vendor" });
            f.Db.AdministrativeUnits.Add(new AdministrativeUnit
            {
                unit_id = 1, unit_type = "WARD", unit_name = "One",
                contact_name = "Ward Office", contact_phone = "0236000000",
            });
            foreach (var id in new long[] { 1, 10, 11 })
                f.Db.UserAccounts.Add(new UserAccount
                {
                    user_id = id,
                    phone_number = $"09000000{id:00}",
                    password_hash = "test-only",
                    full_name = $"User {id}",
                    role_code = id < 10 ? "WARD_AUTHORITY" : "VENDOR",
                    ward_unit_id = 1,
                    account_status = "ACTIVE",
                });
            f.Db.Vendors.AddRange(new Vendor { vendor_id = 10, user_id = 10 }, new Vendor { vendor_id = 11, user_id = 11 });
            f.Db.PricingZones.Add(new PricingZone
            {
                zone_id = 1, ward_unit_id = 1, ward_unit_type = "WARD", zone_name = "Zone", zone_code = "KZ-01", price_per_day = 25000, created_by = 1,
            });
            f.Db.ZoneFeeComponents.AddRange(
                new ZoneFeeComponent { zone_id = 1, component_name = "Deposit", calc_basis = "PER_TERM", unit_amount = 500000, sort_order = 2 },
                new ZoneFeeComponent { zone_id = 1, component_name = "Cleaning", calc_basis = "PER_DAY", unit_amount = 3000, sort_order = 1 });
            f.Db.StreetFeatures.Add(new StreetFeature
            {
                zone_id = 1, feature_type = "TRANSFORMER", label = "Transformer", latitude = 16.06m, longitude = 108.21m, blocks_business = true,
            });
            foreach (var id in new long[] { 1, 2 })
                f.Db.BusinessRegistrations.Add(new BusinessRegistration
                {
                    registration_id = id,
                    vendor_id = id == 1 ? 10 : 11,
                    ward_unit_id = 1,
                    vendor_type = "ITINERANT",
                    display_name = $"Vendor {id}",
                    registration_status = "APPROVED",
                });
            f.Db.SidewalkSlots.AddRange(
                new SidewalkSlot { slot_id = 1, zone_id = 1, slot_code = "S-01", latitude = 16.06m, longitude = 108.21m, slot_status = "AVAILABLE", source = "WARD_DEFINED" },
                new SidewalkSlot { slot_id = 2, zone_id = 1, slot_code = "S-02", latitude = 16.06m, longitude = 108.21m, slot_status = "ACTIVE", source = "WARD_DEFINED" });
            f.Db.RentalApplications.Add(new RentalApplication
            {
                application_id = 1, registration_id = 1, slot_id = 2, application_method = "MANUAL_SELECTED",
                requested_term_days = 30, application_status = "APPROVED",
            });
            f.Db.RentalContracts.Add(new RentalContract
            {
                contract_id = 1, application_id = 1, slot_id = 2, vendor_id = 10,
                start_date = new DateOnly(2026, 9, 1), end_date = new DateOnly(2026, 12, 1), contract_status = "ACTIVE",
            });
            await f.Db.SaveChangesAsync();
            return f;
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
