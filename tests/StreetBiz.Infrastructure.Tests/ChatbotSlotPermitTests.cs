using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Moq;
using StreetBiz.Application.Features.Chatbot;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;
using StreetBiz.Infrastructure.Services.Chatbot;

namespace StreetBiz.Infrastructure.Tests;

public sealed class ChatbotSlotPermitTests
{
    private static readonly ChatbotActor Officer = new(1, 1, "WARD_AUTHORITY", 1, null);

    [Fact]
    public async Task Reads_newest_live_validity_inside_the_officers_ward_only()
    {
        await using var f = await Fixture.Create();
        var permit = await f.Reader.SlotPermitAsync(Officer, "A-01", default);
        Assert.NotNull(permit);
        Assert.Equal("SUSPENDED", permit.EffectiveStatus);
        Assert.Equal(21, permit.ContractId);
        Assert.Equal("Hộ A", permit.VendorName);

        Assert.Null(await f.Reader.SlotPermitAsync(Officer, "B-01", default));
        Assert.Equal("NO_PERMIT", (await f.Reader.SlotPermitAsync(Officer, "A-02", default))!.EffectiveStatus);
        Assert.NotNull(await f.Reader.SlotPermitAsync(Officer with { UserId = 2, WardId = 2 }, "B-01", default));
    }

    [Theory]
    [InlineData("VENDOR")]
    [InlineData("PLATFORM_ADMIN")]
    [InlineData("CUSTOMER")]
    public async Task Other_roles_are_denied(string role)
    {
        await using var f = await Fixture.Create();
        Assert.Equal(403, (await Assert.ThrowsAsync<ChatbotException>(() => f.Reader.SlotPermitAsync(Officer with { Role = role }, "A-01", default))).Status);
    }

    [Fact]
    public async Task Revoked_session_reads_nothing()
    {
        await using var f = await Fixture.Create(active: false);
        Assert.Equal(401, (await Assert.ThrowsAsync<ChatbotException>(() => f.Reader.SlotPermitAsync(Officer, "A-01", default))).Status);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        public TestContext Db = null!;
        public ChatbotListReader Reader = null!;

        public static async Task<Fixture> Create(bool active = true)
        {
            var f = new Fixture(); await f.connection.OpenAsync();
            f.Db = new(new DbContextOptionsBuilder<StreetBizDbContext>().UseSqlite(f.connection).Options);
            await f.Db.Database.EnsureCreatedAsync();
            // The SQL Server view is not created by EnsureCreated; a table with the same shape stands in for it.
            await f.Db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS vw_PermitValidity (permit_id INTEGER, contract_id INTEGER, qr_payload TEXT, slot_id INTEGER,
                  vendor_id INTEGER, start_date TEXT, end_date TEXT, permit_status TEXT, contract_status TEXT, effective_status TEXT)
                """);
            f.Db.Roles.AddRange(new Role { role_code = "WARD_AUTHORITY", role_name = "Ward" }, new Role { role_code = "VENDOR", role_name = "Vendor" });
            f.Db.AdministrativeUnits.AddRange(new AdministrativeUnit { unit_id = 1, unit_type = "WARD", unit_name = "Phường 1" },
                new AdministrativeUnit { unit_id = 2, unit_type = "WARD", unit_name = "Phường 2" });
            f.Db.UserAccounts.AddRange(
                new UserAccount { user_id = 1, phone_number = "0900000001", password_hash = "x", role_code = "WARD_AUTHORITY", ward_unit_id = 1, account_status = "ACTIVE" },
                new UserAccount { user_id = 10, phone_number = "0900000010", password_hash = "x", role_code = "VENDOR", account_status = "ACTIVE" });
            f.Db.Vendors.Add(new Vendor { vendor_id = 10, user_id = 10 });
            f.Db.BusinessRegistrations.Add(new BusinessRegistration { registration_id = 1, vendor_id = 10, ward_unit_id = 1, vendor_type = "ITINERANT", display_name = "Hộ A", registration_status = "APPROVED" });
            f.Db.PricingZones.AddRange(new PricingZone { zone_id = 1, ward_unit_id = 1, zone_name = "Khu A", price_per_day = 50000, created_by = 1 },
                new PricingZone { zone_id = 2, ward_unit_id = 2, zone_name = "Khu B", price_per_day = 50000, created_by = 1 });
            f.Db.SidewalkSlots.AddRange(
                new SidewalkSlot { slot_id = 100, zone_id = 1, slot_code = "A-01", source = "WARD_DEFINED", slot_status = "RENTED", latitude = 16, longitude = 108 },
                new SidewalkSlot { slot_id = 101, zone_id = 1, slot_code = "A-02", source = "WARD_DEFINED", slot_status = "AVAILABLE", latitude = 16, longitude = 108 },
                new SidewalkSlot { slot_id = 200, zone_id = 2, slot_code = "B-01", source = "WARD_DEFINED", slot_status = "RENTED", latitude = 16, longitude = 108 });
            await f.Db.SaveChangesAsync();
            await f.Db.Database.ExecuteSqlRawAsync("""
                INSERT INTO vw_PermitValidity VALUES
                  (1, 20, 'old', 100, 10, '2025-01-01', '2025-12-31', 'ACTIVE', 'EXPIRED', 'EXPIRED'),
                  (2, 21, 'new', 100, 10, '2026-01-01', '2026-12-31', 'SUSPENDED', 'SUSPENDED', 'SUSPENDED'),
                  (3, 30, 'other', 200, 10, '2026-01-01', '2026-12-31', 'ACTIVE', 'ACTIVE', 'VALID')
                """);
            var actors = new Mock<IChatbotActorResolver>();
            actors.Setup(a => a.IsActiveAsync(It.IsAny<ChatbotActor>(), It.IsAny<CancellationToken>())).ReturnsAsync(active);
            f.Reader = new(f.Db, actors.Object);
            return f;
        }

        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await connection.DisposeAsync(); }
    }

    private sealed class TestContext(DbContextOptions<StreetBizDbContext> options) : StreetBizDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            foreach (var entity in builder.Model.GetEntityTypes()) foreach (var p in entity.GetProperties())
            { if (p.GetComputedColumnSql() != null) { p.SetComputedColumnSql(null); p.ValueGenerated = ValueGenerated.Never; } p.SetDefaultValueSql(null); }
        }
    }
}
