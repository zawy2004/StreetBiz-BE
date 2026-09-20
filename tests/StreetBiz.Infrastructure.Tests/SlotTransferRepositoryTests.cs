using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using StreetBiz.Application.Common.Security;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Persistence.Repositories;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Tests;

/// <summary>
/// A transfer request must tell the receiver which slot and term they are being offered, although the
/// receiver cannot read the sender's contract.
/// </summary>
public sealed class SlotTransferRepositoryTests
{
    private const long Sender = 10;
    private const long Receiver = 11;

    [Fact]
    public async Task The_receiver_sees_which_slot_and_term_a_request_is_about()
    {
        using var f = await Fixture.Create();

        var incoming = await new SlotTransferRequestRepository(f.Db).ListByVendorAsync(Receiver, TransferDirections.Incoming, default);

        var row = Assert.Single(incoming);
        Assert.Equal("S-02", row.SlotCode);
        Assert.Equal("Zone", row.ZoneName);
        Assert.Equal(new DateOnly(2026, 9, 1), row.ContractStartDate);
        Assert.Equal(new DateOnly(2026, 12, 1), row.ContractEndDate);
    }

    [Fact]
    public async Task The_sender_sees_the_same_details_on_the_outgoing_side()
    {
        using var f = await Fixture.Create();

        var outgoing = await new SlotTransferRequestRepository(f.Db).ListByVendorAsync(Sender, TransferDirections.Outgoing, default);

        var row = Assert.Single(outgoing);
        Assert.Equal("S-02", row.SlotCode);
        Assert.Equal(1, row.ContractId);
    }

    [Fact]
    public async Task A_single_request_read_by_id_carries_the_slot_details_too()
    {
        using var f = await Fixture.Create();

        var row = await new SlotTransferRequestRepository(f.Db).GetByIdAsync(1, default);

        Assert.Equal("S-02", row!.SlotCode);
        Assert.Equal("Zone", row.ZoneName);
    }

    [Fact]
    public async Task A_third_vendor_sees_nothing_of_the_request()
    {
        using var f = await Fixture.Create();

        var repository = new SlotTransferRequestRepository(f.Db);

        Assert.Empty(await repository.ListByVendorAsync(12, TransferDirections.Incoming, default));
        Assert.Empty(await repository.ListByVendorAsync(12, TransferDirections.Outgoing, default));
    }

    private sealed class Fixture : IDisposable
    {
        private SqliteConnection Connection { get; } = new("Data Source=:memory:");
        public TestContext Db { get; private set; } = null!;

        public static async Task<Fixture> Create()
        {
            var f = new Fixture();
            await f.Connection.OpenAsync();
            f.Db = new TestContext(new DbContextOptionsBuilder<StreetBizDbContext>().UseSqlite(f.Connection).Options);
            await f.Db.Database.EnsureCreatedAsync();

            f.Db.Roles.AddRange(new Role { role_code = "WARD_AUTHORITY", role_name = "Ward" },
                new Role { role_code = "VENDOR", role_name = "Vendor" });
            f.Db.AdministrativeUnits.Add(new AdministrativeUnit { unit_id = 1, unit_type = "WARD", unit_name = "One" });
            foreach (var id in new long[] { 1, Sender, Receiver, 12 })
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
            f.Db.Vendors.AddRange(
                new Vendor { vendor_id = Sender, user_id = Sender },
                new Vendor { vendor_id = Receiver, user_id = Receiver },
                new Vendor { vendor_id = 12, user_id = 12 });
            f.Db.PricingZones.Add(new PricingZone
            {
                zone_id = 1, ward_unit_id = 1, ward_unit_type = "WARD", zone_name = "Zone", price_per_day = 25000, created_by = 1,
            });
            f.Db.SidewalkSlots.Add(new SidewalkSlot
            {
                slot_id = 2, zone_id = 1, slot_code = "S-02", latitude = 16.06m, longitude = 108.21m, slot_status = "ACTIVE", source = "WARD_DEFINED",
            });
            f.Db.BusinessRegistrations.Add(new BusinessRegistration
            {
                registration_id = 1, vendor_id = Sender, ward_unit_id = 1, vendor_type = "ITINERANT", display_name = "Sender", registration_status = "APPROVED",
            });
            f.Db.RentalApplications.Add(new RentalApplication
            {
                application_id = 1, registration_id = 1, slot_id = 2, application_method = "MANUAL_SELECTED",
                requested_term_days = 90, application_status = "APPROVED",
            });
            f.Db.RentalContracts.Add(new RentalContract
            {
                contract_id = 1, application_id = 1, slot_id = 2, vendor_id = Sender,
                start_date = new DateOnly(2026, 9, 1), end_date = new DateOnly(2026, 12, 1), contract_status = "ACTIVE",
            });
            f.Db.SlotTransferRequests.Add(new SlotTransferRequest
            {
                transfer_id = 1, contract_id = 1, from_vendor_id = Sender, to_vendor_id = Receiver,
                transfer_status = TransferStatuses.Pending, initiated_at = new DateTime(2026, 9, 20, 3, 0, 0, DateTimeKind.Utc),
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
