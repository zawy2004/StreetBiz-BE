using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Moq;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Features.WardSlots;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;
using StreetBiz.Infrastructure.Services;

namespace StreetBiz.Infrastructure.Tests;

public sealed class WardSlotsTests
{
    [Fact]
    public async Task Approval_is_audited_and_cannot_be_repeated()
    {
        using var f = await Fixture.Create();
        var result = await f.Service.DecideAsync(f.Actor, "proposals", 1, new("APPROVE", "Đã kiểm tra", "PENDING"), default);
        Assert.Equal("APPROVED", result.Status);
        Assert.Single(await f.Db.Notifications.ToListAsync());
        Assert.Single(await f.Db.AuditLogs.ToListAsync());
        var error = await Assert.ThrowsAsync<WardException>(() =>
            f.Service.DecideAsync(f.Actor, "proposals", 1, new("APPROVE", "Duyệt lại", "PENDING"), default));
        Assert.Equal(409, error.Status);
        Assert.Single(await f.Db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task Other_ward_cannot_read_or_review_case()
    {
        using var f = await Fixture.Create();
        var foreignActor = new WardActor(2, 2, "Other");
        Assert.Equal(404, (await Assert.ThrowsAsync<WardException>(() =>
            f.Service.GetAsync(foreignActor, "proposals", 1, default))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<WardException>(() =>
            f.Service.DecideAsync(foreignActor, "proposals", 1, new("REJECT", "X", "PENDING"), default))).Status);
        Assert.Empty(await f.Db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task Suspended_actor_is_denied()
    {
        using var f = await Fixture.Create();
        (await f.Db.UserAccounts.FindAsync(1L))!.account_status = "SUSPENDED";
        await f.Db.SaveChangesAsync();
        Assert.Equal(403, (await Assert.ThrowsAsync<WardException>(() =>
            f.Service.GetAsync(f.Actor, "proposals", 1, default))).Status);
    }

    [Fact]
    public async Task Outside_geofence_blocks_approval_but_allows_rejection()
    {
        using var f = await Fixture.Create();
        f.Geo.Setup(x => x.Verify(1, It.IsAny<GeoPoint>())).Returns(new GeofenceResult(false, 1, "test"));
        Assert.Equal(409, (await Assert.ThrowsAsync<WardException>(() =>
            f.Service.DecideAsync(f.Actor, "proposals", 1, new("APPROVE", "X", "PENDING"), default))).Status);
        var rejected = await f.Service.DecideAsync(f.Actor, "proposals", 1, new("REJECT", "Ngoài phường", "PENDING"), default);
        Assert.Equal("REJECTED", rejected.Status);
        Assert.Equal("AVAILABLE", (await f.Db.SidewalkSlots.FindAsync(1L))!.slot_status);
    }

    [Fact]
    public async Task Pin_is_validated_and_persisted_at_database_precision()
    {
        using var f = await Fixture.Create();
        var result = await f.Service.PinAsync(f.Actor, 1, new(10.1234567, 106.1234567), default);
        Assert.Equal(10.123457, result.Location!.Latitude);
        f.Geo.Verify(x => x.Verify(1, new GeoPoint(10.123457, 106.123457)), Times.AtLeastOnce());
        f.Geo.Setup(x => x.Verify(1, It.IsAny<GeoPoint>())).Returns(new GeofenceResult(false, 1, "test"));
        Assert.Equal(422, (await Assert.ThrowsAsync<WardException>(() =>
            f.Service.PinAsync(f.Actor, 1, new(20, 110), default))).Status);
    }

    [Fact]
    public async Task Queue_does_not_release_or_reassign_occupied_contract()
    {
        using var f = await Fixture.Create();
        var result = await f.Service.DecideAsync(f.Actor, "conflicts", 1, new("QUEUE", "Đợi ô trống", "PENDING"), default);
        Assert.Equal("UNDER_REVIEW", result.Status);
        Assert.Equal(1, result.QueuePosition);
        var contract = await f.Db.RentalContracts.FindAsync(1L);
        Assert.Equal(10, contract!.vendor_id);
        Assert.Equal("ACTIVE", contract.contract_status);
        Assert.Equal("Original address", (await f.Db.BusinessRegistrations.FindAsync(1L))!.declared_address);
        Assert.Equal(409, (await Assert.ThrowsAsync<WardException>(() =>
            f.Service.DecideAsync(f.Actor, "conflicts", 1, new("QUEUE", "Again", "UNDER_REVIEW"), default))).Status);
    }

    [Fact]
    public async Task Conflict_without_occupant_cannot_be_queued()
    {
        using var f = await Fixture.Create();
        (await f.Db.RentalContracts.FindAsync(1L))!.contract_status = "EXPIRED";
        await f.Db.SaveChangesAsync();
        Assert.Equal(409, (await Assert.ThrowsAsync<WardException>(() =>
            f.Service.DecideAsync(f.Actor, "conflicts", 1, new("QUEUE", "X", "PENDING"), default))).Status);
    }

    [Fact]
    public async Task Transfer_preserves_dates_fees_and_permit_and_notifies_both_parties()
    {
        using var f = await Fixture.Create();
        var before = await f.Db.RentalContracts.AsNoTracking().SingleAsync();
        var result = await f.Service.DecideAsync(f.Actor, "transfers", 1,
            new("APPROVE", "Đủ điều kiện", "ACCEPTED_BY_RECEIVER"), default);
        var after = await f.Db.RentalContracts.AsNoTracking().SingleAsync();
        Assert.Equal("APPROVED", result.Status);
        Assert.Equal(11, after.vendor_id);
        Assert.Equal(before.start_date, after.start_date);
        Assert.Equal(before.end_date, after.end_date);
        Assert.Equal(before.application_id, after.application_id);
        Assert.Equal(100m, (await f.Db.FeeSchedules.SingleAsync()).total_amount);
        Assert.Equal(1, (await f.Db.DigitalPermits.SingleAsync()).contract_id);
        Assert.Equal(2, await f.Db.Notifications.CountAsync());
        Assert.Single(await f.Db.AuditLogs.ToListAsync());
    }

    [Theory]
    [InlineData("PENDING")]
    [InlineData("OVERDUE")]
    public async Task Unpaid_fee_blocks_transfer(string status)
    {
        using var f = await Fixture.Create();
        (await f.Db.FeeScheduleItems.SingleAsync()).item_status = status;
        await f.Db.SaveChangesAsync();
        var result = await f.Service.GetAsync(f.Actor, "transfers", 1, default);
        Assert.Equal(100m, result.Outstanding);
        Assert.DoesNotContain("APPROVE", result.Actions);
        Assert.Equal(409, (await Assert.ThrowsAsync<WardException>(() =>
            f.Service.DecideAsync(f.Actor, "transfers", 1, new("APPROVE", "X", "ACCEPTED_BY_RECEIVER"), default))).Status);
        Assert.Equal(10, (await f.Db.RentalContracts.AsNoTracking().SingleAsync()).vendor_id);
    }

    [Theory]
    [InlineData("receiver")]
    [InlineData("acceptance")]
    [InlineData("expiry")]
    [InlineData("owner")]
    public async Task Invalid_transfer_preconditions_block_approval(string failure)
    {
        using var f = await Fixture.Create();
        if (failure == "receiver") (await f.Db.BusinessRegistrations.FindAsync(2L))!.registration_status = "SUBMITTED";
        if (failure == "acceptance") (await f.Db.SlotTransferRequests.FindAsync(1L))!.accepted_at = null;
        if (failure == "expiry") (await f.Db.RentalContracts.FindAsync(1L))!.end_date = new DateOnly(2000, 1, 1);
        if (failure == "owner") (await f.Db.RentalContracts.FindAsync(1L))!.vendor_id = 11;
        await f.Db.SaveChangesAsync();
        Assert.DoesNotContain("APPROVE", (await f.Service.GetAsync(f.Actor, "transfers", 1, default)).Actions);
    }

    [Fact]
    public async Task Missing_reason_is_rejected_without_side_effects()
    {
        using var f = await Fixture.Create();
        Assert.Equal(400, (await Assert.ThrowsAsync<WardException>(() =>
            f.Service.DecideAsync(f.Actor, "proposals", 1, new("REJECT", " ", "PENDING"), default))).Status);
        Assert.Empty(await f.Db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task Unpaid_slot_penalty_blocks_transfer_even_without_contract_reference()
    {
        using var f = await Fixture.Create();
        f.Db.ViolationTypes.Add(new ViolationType { violation_type_code = "TEST", description = "Test", is_active = true });
        f.Db.PenaltyFeeSchedules.Add(new PenaltyFeeSchedule
        {
            penalty_schedule_id = 1,
            ward_unit_id = 1,
            violation_type = "TEST",
            penalty_amount = 10,
            created_by = 1
        });
        f.Db.Violations.Add(new Violation
        {
            violation_id = 1,
            slot_id = 2,
            vendor_id = 10,
            violation_type = "TEST",
            recorded_by = 1,
            source = "ON_SITE"
        });
        f.Db.Penalties.Add(new Penalty
        {
            penalty_id = 1,
            violation_id = 1,
            penalty_schedule_id = 1,
            amount = 10,
            penalty_status = "UNPAID"
        });
        await f.Db.SaveChangesAsync();
        var result = await f.Service.GetAsync(f.Actor, "transfers", 1, default);
        Assert.Equal(10m, result.Outstanding);
        Assert.DoesNotContain("APPROVE", result.Actions);
    }

    [Fact]
    public async Task Fixed_storefront_receiver_requires_configured_adjacency()
    {
        using var f = await Fixture.Create();
        var registration = (await f.Db.BusinessRegistrations.FindAsync(2L))!;
        registration.vendor_type = "FIXED_STOREFRONT";
        registration.address_latitude = 10;
        registration.address_longitude = 106;
        await f.Db.SaveChangesAsync();
        Assert.DoesNotContain("APPROVE", (await f.Service.GetAsync(f.Actor, "transfers", 1, default)).Actions);
        var policy = Mock.Of<ISidewalkPolicy>(x => x.AdjacentRadiusMeters == 50);
        var service = new WardSlots(f.Db, f.Geo.Object, policy, TimeProvider.System);
        Assert.Contains("APPROVE", (await service.GetAsync(f.Actor, "transfers", 1, default)).Actions);
    }

    [Fact]
    public async Task Ward_list_excludes_foreign_records_and_invalid_kind_is_rejected()
    {
        using var f = await Fixture.Create();
        Assert.Empty((await f.Service.ListAsync(new WardActor(2, 2, "Other"), "proposals", 1, default)).Items);
        Assert.Single((await f.Service.ListAsync(f.Actor, "proposals", 1, default)).Items);
        Assert.Equal(400, (await Assert.ThrowsAsync<WardException>(() =>
            f.Service.ListAsync(f.Actor, "unknown", 1, default))).Status);
    }

    private sealed class Fixture : IDisposable
    {
        public SqliteConnection Connection { get; } = new("Data Source=:memory:");
        public TestContext Db { get; private set; } = null!;
        public Mock<IGeolocation> Geo { get; } = new();
        public Mock<ISidewalkPolicy> SidewalkPolicy { get; } = new();
        public WardActor Actor { get; } = new(1, 1, "Ward");
        public WardSlots Service { get; private set; } = null!;

        public static async Task<Fixture> Create()
        {
            var f = new Fixture();
            await f.Connection.OpenAsync();
            f.Db = new TestContext(new DbContextOptionsBuilder<StreetBizDbContext>().UseSqlite(f.Connection).Options);
            await f.Db.Database.EnsureCreatedAsync();
            f.Geo.Setup(x => x.Verify(It.IsAny<int>(), It.IsAny<GeoPoint>())).Returns(new GeofenceResult(true, 1, "test"));
            f.SidewalkPolicy.SetupGet(x => x.AdjacentRadiusMeters).Returns(0);
            f.Service = new(f.Db, f.Geo.Object, f.SidewalkPolicy.Object, TimeProvider.System);
            f.Db.Roles.AddRange(new Role { role_code = "WARD_AUTHORITY", role_name = "Ward" },
                new Role { role_code = "VENDOR", role_name = "Vendor" });
            f.Db.AdministrativeUnits.AddRange(new AdministrativeUnit { unit_id = 1, unit_type = "WARD", unit_name = "One" },
                new AdministrativeUnit { unit_id = 2, unit_type = "WARD", unit_name = "Two" });
            foreach (var id in new long[] { 1, 2, 10, 11 })
                f.Db.UserAccounts.Add(new UserAccount
                {
                    user_id = id,
                    phone_number = $"09000000{id:00}",
                    password_hash = "test-only",
                    full_name = $"User {id}",
                    role_code = id < 10 ? "WARD_AUTHORITY" : "VENDOR",
                    ward_unit_id = id == 2 ? 2 : 1,
                    account_status = "ACTIVE"
                });
            f.Db.Vendors.AddRange(new Vendor { vendor_id = 10, user_id = 10 }, new Vendor { vendor_id = 11, user_id = 11 });
            f.Db.PricingZones.Add(new PricingZone { zone_id = 1, ward_unit_id = 1, zone_name = "Zone", created_by = 1 });
            foreach (var id in new long[] { 1, 2 })
                f.Db.BusinessRegistrations.Add(new BusinessRegistration
                {
                    registration_id = id,
                    vendor_id = id == 1 ? 10 : 11,
                    ward_unit_id = 1,
                    vendor_type = "ITINERANT",
                    display_name = $"Vendor {id}",
                    declared_address = "Original address",
                    registration_status = "APPROVED"
                });
            f.Db.SidewalkSlots.AddRange(
                new SidewalkSlot
                {
                    slot_id = 1,
                    zone_id = 1,
                    slot_code = "PROPOSAL",
                    source = "VENDOR_PROPOSED",
                    proposed_by_registration_id = 1,
                    proposal_review_status = "PENDING",
                    slot_status = "AVAILABLE",
                    latitude = 10,
                    longitude = 106,
                    proposal_photo_url = "https://example.test/evidence"
                },
                new SidewalkSlot
                {
                    slot_id = 2,
                    zone_id = 1,
                    slot_code = "RENTED",
                    source = "WARD_DEFINED",
                    slot_status = "ACTIVE",
                    latitude = 10,
                    longitude = 106
                });
            f.Db.RentalApplications.Add(new RentalApplication
            {
                application_id = 1,
                registration_id = 1,
                slot_id = 2,
                application_method = "MANUAL_SELECTED",
                application_status = "APPROVED",
                requested_term_days = 365
            });
            f.Db.RentalContracts.Add(new RentalContract
            {
                contract_id = 1,
                application_id = 1,
                slot_id = 2,
                vendor_id = 10,
                start_date = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-10)),
                end_date = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(365)),
                contract_status = "ACTIVE"
            });
            f.Db.DigitalPermits.Add(new DigitalPermit { permit_id = 1, contract_id = 1, qr_payload = "test-only", permit_status = "ACTIVE" });
            f.Db.FeeSchedules.Add(new FeeSchedule { fee_schedule_id = 1, contract_id = 1, total_amount = 100 });
            f.Db.FeeScheduleItems.Add(new FeeScheduleItem { fee_item_id = 1, fee_schedule_id = 1, amount = 100, item_status = "PAID" });
            f.Db.SlotTransferRequests.Add(new SlotTransferRequest
            {
                transfer_id = 1,
                contract_id = 1,
                from_vendor_id = 10,
                to_vendor_id = 11,
                transfer_status = "ACCEPTED_BY_RECEIVER",
                accepted_at = DateTime.UtcNow
            });
            f.Db.AddressChangeRequests.Add(new AddressChangeRequest
            {
                address_change_id = 1,
                registration_id = 1,
                new_address = "New address",
                requested_new_slot_id = 2,
                change_status = "PENDING"
            });
            await f.Db.SaveChangesAsync();
            f.Db.ChangeTracker.Clear();
            return f;
        }
        public void Dispose() { Db.Dispose(); Connection.Dispose(); }
    }

    // Tests execute real relational transactions and FK constraints in an isolated in-memory DB.
    // SQL Server-generated defaults/computed columns are replaced only in this test model.
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
