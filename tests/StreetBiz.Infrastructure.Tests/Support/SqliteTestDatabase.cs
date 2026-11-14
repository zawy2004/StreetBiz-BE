using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Tests.Support;

/// <summary>
/// An in-memory SQLite copy of the EF model for repository tests. Unlike the per-file fixtures
/// that strip every SQL default, this one translates the SQL Server defaults the schema uses
/// (<c>sysutcdatetime()</c>) so code that relies on a database default -- created_at,
/// initiated_at -- behaves as it does against SQL Server.
/// </summary>
public sealed class SqliteTestDatabase : IDisposable
{
    public const int Ward = 1;
    public const long Officer = 1;
    public const long VendorA = 10;
    public const long VendorB = 11;
    public const long VendorC = 12;
    public const long Customer = 20;
    public const int Zone = 1;
    public const long SlotRented = 1;
    public const long SlotFree = 2;
    public const long SlotFree2 = 3;
    public const long RegistrationA = 1;
    public const long RegistrationB = 2;
    public const long ApplicationA = 1;
    public const long ContractA = 1;

    private readonly SqliteConnection connection = new("Data Source=:memory:");

    public StreetBizDbContext Db { get; private set; } = null!;

    public static async Task<SqliteTestDatabase> CreateAsync(bool seed = true)
    {
        var database = new SqliteTestDatabase();
        await database.connection.OpenAsync();
        database.Db = database.NewContext();
        await database.Db.Database.EnsureCreatedAsync();
        if (seed)
        {
            await database.SeedAsync();
        }

        return database;
    }

    /// <summary>A second context on the same connection, for reading what another context wrote.</summary>
    public StreetBizDbContext NewContext() =>
        new SqliteContext(new DbContextOptionsBuilder<StreetBizDbContext>().UseSqlite(connection).Options);

    /// <summary>A context on the same connection with EF interceptors attached (e.g. the push interceptor).</summary>
    public StreetBizDbContext NewContext(params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors) =>
        new SqliteContext(new DbContextOptionsBuilder<StreetBizDbContext>().UseSqlite(connection).AddInterceptors(interceptors).Options);

    private async Task SeedAsync()
    {
        Db.Roles.AddRange(
            new Role { role_code = "WARD_AUTHORITY", role_name = "Ward" },
            new Role { role_code = "VENDOR", role_name = "Vendor" },
            new Role { role_code = "CUSTOMER", role_name = "Customer" });
        Db.AdministrativeUnits.Add(new AdministrativeUnit { unit_id = Ward, unit_type = "WARD", unit_name = "Phường Một" });
        foreach (var (id, role) in new[] { (Officer, "WARD_AUTHORITY"), (VendorA, "VENDOR"), (VendorB, "VENDOR"), (VendorC, "VENDOR"), (Customer, "CUSTOMER") })
        {
            Db.UserAccounts.Add(new UserAccount
            {
                user_id = id,
                phone_number = $"09000000{id:00}",
                password_hash = "test-only",
                full_name = $"User {id}",
                role_code = role,
                ward_unit_id = Ward,
                account_status = "ACTIVE",
            });
        }

        Db.Vendors.AddRange(
            new Vendor { vendor_id = VendorA, user_id = VendorA },
            new Vendor { vendor_id = VendorB, user_id = VendorB },
            new Vendor { vendor_id = VendorC, user_id = VendorC });
        Db.PricingZones.Add(new PricingZone
        {
            zone_id = Zone, ward_unit_id = Ward, ward_unit_type = "WARD", zone_name = "Nguyễn Văn Linh", price_per_day = 30000, created_by = Officer,
        });
        Db.SidewalkSlots.AddRange(
            Slot(SlotRented, "S-01", "ACTIVE", 16.0610m),
            Slot(SlotFree, "S-02", "AVAILABLE", 16.0612m),
            Slot(SlotFree2, "S-03", "AVAILABLE", 16.0700m));
        Db.BusinessRegistrations.AddRange(
            new BusinessRegistration
            {
                registration_id = RegistrationA, vendor_id = VendorA, ward_unit_id = Ward, vendor_type = "FIXED_STOREFRONT",
                display_name = "Quán A", registration_status = "APPROVED",
            },
            new BusinessRegistration
            {
                registration_id = RegistrationB, vendor_id = VendorB, ward_unit_id = Ward, vendor_type = "ITINERANT",
                display_name = "Xe B", registration_status = "APPROVED",
            });
        Db.RentalApplications.Add(new RentalApplication
        {
            application_id = ApplicationA, registration_id = RegistrationA, slot_id = SlotRented, application_method = "AUTO_ADJACENT",
            requested_term_days = 90, application_status = "APPROVED", created_at = new DateTime(2026, 9, 1, 2, 0, 0, DateTimeKind.Utc),
        });
        Db.RentalContracts.Add(new RentalContract
        {
            contract_id = ContractA, application_id = ApplicationA, slot_id = SlotRented, vendor_id = VendorA,
            start_date = new DateOnly(2026, 9, 1), end_date = new DateOnly(2026, 12, 1), contract_status = "ACTIVE",
        });
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
    }

    private static SidewalkSlot Slot(long id, string code, string status, decimal latitude) => new()
    {
        slot_id = id, zone_id = Zone, slot_code = code, latitude = latitude, longitude = 108.2180m,
        slot_status = status, source = "WARD_DEFINED", width_meters = 2m, length_meters = 3m,
    };

    public void Dispose()
    {
        Db.Dispose();
        connection.Dispose();
    }

    private sealed class SqliteContext(DbContextOptions<StreetBizDbContext> options) : StreetBizDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            foreach (var entity in modelBuilder.Model.GetEntityTypes())
            {
                foreach (var property in entity.GetProperties())
                {
                    if (property.GetComputedColumnSql() is not null)
                    {
                        property.SetComputedColumnSql(null);
                        property.ValueGenerated = ValueGenerated.Never;
                    }

                    var sql = property.GetDefaultValueSql();
                    if (sql is null)
                    {
                        continue;
                    }

                    // SQL Server -> SQLite for the two default expressions the schema uses.
                    property.SetDefaultValueSql(sql.Contains("CONVERT([date]", StringComparison.OrdinalIgnoreCase)
                        ? "(date('now'))"
                        : sql.Contains("sysutcdatetime", StringComparison.OrdinalIgnoreCase)
                            ? "(strftime('%Y-%m-%d %H:%M:%f','now'))"
                            : null);
                }
            }
        }
    }
}

/// <summary>A clock that answers whatever the test sets, for both clock abstractions in use.</summary>
public sealed class TestClock(DateTime utcNow) : TimeProvider, IDateTimeProvider
{
    public DateTime UtcNow { get; set; } = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);

    public override DateTimeOffset GetUtcNow() => new(UtcNow);
}
