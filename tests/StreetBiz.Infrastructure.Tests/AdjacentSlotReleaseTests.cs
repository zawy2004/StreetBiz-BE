using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Moq;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.AddressChanges.ReleaseAdjacentSlots;
using StreetBiz.Application.Features.WardSlots;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Persistence.Repositories;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;
using StreetBiz.Infrastructure.Services;

namespace StreetBiz.Infrastructure.Tests;

/// <summary>
/// SIDE-10 / BR-22 on a real (SQLite) schema: the previous adjacent slot goes back after the grace
/// period, only when nothing is owed on it, and the ward can extend the period or let the vendor keep it.
/// </summary>
public sealed class AdjacentSlotReleaseTests
{
    // The change is requested on 1 Oct 2026 (Da Nang), so with the 7-day grace its last day is 8 Oct.
    private static readonly DateTime RequestedAt = new(2026, 10, 1, 3, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly LastGraceDay = new(2026, 10, 8);
    private static readonly DateOnly DayAfter = new(2026, 10, 9);

    [Fact]
    public async Task The_grace_period_runs_seven_days_from_the_request_and_the_slot_goes_back_the_day_after()
    {
        using var f = await Fixture.Create();

        Assert.Equal(LastGraceDay, AdjacentSlotRelease.GraceEndsOn(RequestedAt, 0));
        Assert.Equal(LastGraceDay.AddDays(7), AdjacentSlotRelease.GraceEndsOn(RequestedAt, 7));
        // 8 Oct 20:00 UTC is already 9 Oct in Da Nang; the request itself is counted on its Vietnamese day.
        Assert.Equal(new DateOnly(2026, 10, 9), AdjacentSlotRelease.GraceEndsOn(new DateTime(2026, 10, 1, 20, 0, 0, DateTimeKind.Utc), 0));
        Assert.False(AdjacentSlotRelease.IsDue(LastGraceDay, LastGraceDay));
        Assert.True(AdjacentSlotRelease.IsDue(LastGraceDay, DayAfter));
    }

    [Fact]
    public async Task The_vendors_list_says_the_slot_is_waiting_and_until_when()
    {
        using var f = await Fixture.Create();

        var row = Assert.Single(await f.Changes.ListByVendorAsync(10, default));

        Assert.Equal("PENDING_RELEASE", row.ReleaseState);
        Assert.Equal("OLD-SLOT", row.ReleasedSlotCode);
        Assert.Equal(LastGraceDay, row.ReleaseGraceEndsOn);
        Assert.False(row.ReleaseBlockedByDebt);
    }

    [Fact]
    public async Task Before_the_grace_period_ends_nothing_is_released()
    {
        using var f = await Fixture.Create();

        var result = await f.Sweep(LastGraceDay);

        Assert.Equal(0, result.Released);
        Assert.Equal("ACTIVE", f.Db.RentalContracts.AsNoTracking().Single(c => c.contract_id == 1).contract_status);
    }

    [Fact]
    public async Task After_the_grace_period_with_nothing_owed_the_slot_goes_back_and_the_vendor_is_told()
    {
        using var f = await Fixture.Create();
        f.Db.FeeScheduleItems.Add(new FeeScheduleItem { fee_item_id = 2, fee_schedule_id = 1, due_date = new DateOnly(2026, 12, 1), amount = 50, item_status = "PENDING" });
        f.Db.RenewalRequests.Add(new RenewalRequest { renewal_id = 1, contract_id = 1, requested_term_days = 30, renewal_status = "PENDING", created_at = RequestedAt });
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();

        var result = await f.Sweep(DayAfter);

        Assert.Equal(1, result.Released);
        f.Db.ChangeTracker.Clear();
        var contract = f.Db.RentalContracts.Include(c => c.slot).Single(c => c.contract_id == 1);
        Assert.Equal("CANCELLED", contract.contract_status);
        // Recorded under the vendor's own login, as a return of the slot by them.
        Assert.Equal(1010, contract.cancelled_by);
        Assert.Equal("AVAILABLE", contract.slot.slot_status);
        // Instalments not yet due are dropped, like any voluntary return (SIDE-07).
        Assert.Equal("CANCELLED", f.Db.FeeScheduleItems.Single(i => i.fee_item_id == 2).item_status);
        Assert.Equal("PAID", f.Db.FeeScheduleItems.Single(i => i.fee_item_id == 1).item_status);
        Assert.Equal("WITHDRAWN", f.Db.RenewalRequests.Single().renewal_status);
        var notice = Assert.Single(f.Db.Notifications.Where(n => n.notification_type == "SLOT_RELEASED"));
        Assert.Equal(1000L + 10, notice.user_id);
        Assert.Contains("OLD-SLOT", notice.body);
        Assert.Single(f.Db.AuditLogs.Where(a => a.action == "SIDE10_RELEASE" && a.entity_id == 1));

        // The next run finds nothing: a released contract is no longer pending.
        Assert.Equal(0, (await f.Sweep(DayAfter.AddDays(1))).Released);
        Assert.Single(f.Db.Notifications.Where(n => n.notification_type == "SLOT_RELEASED"));
        Assert.Equal("RELEASED", Assert.Single(await f.Changes.ListByVendorAsync(10, default)).ReleaseState);
    }

    [Fact]
    public async Task An_overdue_fee_holds_the_slot_back_and_the_vendor_is_reminded_once_a_day()
    {
        using var f = await Fixture.Create();
        f.Db.FeeScheduleItems.Add(new FeeScheduleItem { fee_item_id = 2, fee_schedule_id = 1, due_date = new DateOnly(2026, 9, 20), amount = 75_000, item_status = "OVERDUE" });
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();

        var first = await f.Sweep(DayAfter);
        var again = await f.Sweep(DayAfter);

        Assert.Equal(0, first.Released);
        Assert.Equal(1, first.WaitingForPayment);
        Assert.Equal(1, again.WaitingForPayment);
        Assert.Equal("ACTIVE", f.Db.RentalContracts.AsNoTracking().Single().contract_status);
        // One notice per calendar day: the second run on the same day adds none.
        Assert.Equal(1, f.Db.Notifications.Count(n => n.notification_type == "SLOT_RELEASE_BLOCKED"));

        // The next day's sweep reminds again. (Notifications are stamped with the sweep clock, so the
        // new day is simulated by moving it as well as the date handed to the sweep.)
        f.Clock.Now = f.Clock.Now.AddDays(1);
        var nextDay = await f.Sweep(DayAfter.AddDays(1));
        Assert.Equal(1, nextDay.WaitingForPayment);
        Assert.Equal(2, f.Db.Notifications.Count(n => n.notification_type == "SLOT_RELEASE_BLOCKED"));

        var tracked = Assert.Single(await f.Changes.ListByVendorAsync(10, default));
        Assert.True(tracked.ReleaseBlockedByDebt);
    }

    [Fact]
    public async Task Once_the_debt_is_paid_the_next_sweep_releases_the_slot()
    {
        using var f = await Fixture.Create();
        f.Db.FeeScheduleItems.Add(new FeeScheduleItem { fee_item_id = 2, fee_schedule_id = 1, due_date = new DateOnly(2026, 9, 20), amount = 75_000, item_status = "OVERDUE" });
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();
        await f.Sweep(DayAfter);

        f.Db.FeeScheduleItems.Single(i => i.fee_item_id == 2).item_status = "PAID";
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();
        var result = await f.Sweep(DayAfter.AddDays(1));

        Assert.Equal(1, result.Released);
        Assert.Equal("CANCELLED", f.Db.RentalContracts.AsNoTracking().Single().contract_status);
    }

    [Fact]
    public async Task An_unpaid_penalty_holds_the_slot_back_too()
    {
        using var f = await Fixture.Create();
        f.Db.ViolationTypes.Add(new ViolationType { violation_type_code = "HYGIENE_LITTERING", description = "x", is_active = true });
        f.Db.PenaltyFeeSchedules.Add(new PenaltyFeeSchedule { penalty_schedule_id = 1, ward_unit_id = 1, violation_type = "HYGIENE_LITTERING", penalty_amount = 200_000, effective_from = new DateOnly(2026, 1, 1), created_by = 1 });
        f.Db.Violations.Add(new Violation { violation_id = 1, contract_id = 1, vendor_id = 10, violation_type = "HYGIENE_LITTERING", recorded_by = 1, source = "WARD_INSPECTION", recorded_at = RequestedAt });
        f.Db.Penalties.Add(new Penalty { penalty_id = 1, violation_id = 1, penalty_schedule_id = 1, amount = 200_000, penalty_status = "UNPAID", created_at = RequestedAt });
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();

        var result = await f.Sweep(DayAfter);

        Assert.Equal(1, result.WaitingForPayment);
        Assert.Equal("ACTIVE", f.Db.RentalContracts.AsNoTracking().Single().contract_status);
    }

    [Theory]
    [InlineData("REJECTED")]
    [InlineData("WITHDRAWN")]
    public async Task A_request_that_is_no_longer_open_never_takes_the_slot(string status)
    {
        using var f = await Fixture.Create();
        f.Db.AddressChangeRequests.Single().change_status = status;
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();

        var result = await f.Sweep(DayAfter.AddDays(30));

        Assert.Equal(0, result.Released);
        Assert.Equal("ACTIVE", f.Db.RentalContracts.AsNoTracking().Single().contract_status);
    }

    [Fact]
    public async Task A_request_that_names_no_slot_to_give_back_is_left_alone()
    {
        using var f = await Fixture.Create();
        f.Db.AddressChangeRequests.Single().released_contract_id = null;
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();

        Assert.Equal(0, (await f.Sweep(DayAfter.AddDays(30))).Released);
        var row = Assert.Single(await f.Changes.ListByVendorAsync(10, default));
        Assert.Null(row.ReleaseState);
    }

    [Fact]
    public async Task The_ward_sees_the_waiting_slot_and_can_extend_or_cancel_but_only_its_own_ward()
    {
        using var f = await Fixture.Create();

        var view = await f.Ward.GetAsync(f.Officer, "conflicts", 1, default);

        Assert.Contains("OLD-SLOT", view.Summary);
        Assert.Contains("08/10/2026", view.Summary);
        Assert.Equal(["REJECT", "EXTEND_GRACE", "CANCEL_RELEASE"], view.Actions);
        Assert.Equal(0m, view.Outstanding);
        // A release-only request (no new slot) is in the ward's list.
        Assert.Single((await f.Ward.ListAsync(f.Officer, "conflicts", 1, default)).Items);
        await Assert.ThrowsAsync<WardException>(() => f.Ward.GetAsync(new WardActor(2, 2, "Phường khác"), "conflicts", 1, default));
    }

    [Fact]
    public async Task Extending_the_grace_moves_the_release_a_week_later_and_tells_the_vendor()
    {
        using var f = await Fixture.Create();

        var result = await f.Ward.DecideAsync(f.Officer, "conflicts", 1, new("EXTEND_GRACE", "Đang dọn hàng", "PENDING"), default);

        Assert.Equal("PENDING", result.Status);
        Assert.Contains("15/10/2026", result.Summary);
        Assert.Equal(new DateOnly(2026, 10, 15), Assert.Single(await f.Changes.ListByVendorAsync(10, default)).ReleaseGraceEndsOn);
        Assert.Single(f.Db.AuditLogs.Where(a => a.action == "WARD_EXTEND_GRACE" && a.entity_id == 1));
        Assert.Contains(f.Db.Notifications, n => n.user_id == 1010 && n.title == "Gia hạn thời gian trả ô cũ");

        Assert.Equal(0, (await f.Sweep(DayAfter)).Released);
        Assert.Equal(0, (await f.Sweep(new DateOnly(2026, 10, 15))).Released);
        Assert.Equal(1, (await f.Sweep(new DateOnly(2026, 10, 16))).Released);
    }

    [Fact]
    public async Task Each_extension_adds_another_week()
    {
        using var f = await Fixture.Create();

        await f.Ward.DecideAsync(f.Officer, "conflicts", 1, new("EXTEND_GRACE", "Lần 1", "PENDING"), default);
        await f.Ward.DecideAsync(f.Officer, "conflicts", 1, new("EXTEND_GRACE", "Lần 2", "PENDING"), default);

        Assert.Equal(new DateOnly(2026, 10, 22), Assert.Single(await f.Changes.ListByVendorAsync(10, default)).ReleaseGraceEndsOn);
    }

    [Fact]
    public async Task Cancelling_the_release_lets_the_vendor_keep_the_slot_for_good()
    {
        using var f = await Fixture.Create();

        await f.Ward.DecideAsync(f.Officer, "conflicts", 1, new("CANCEL_RELEASE", "Chưa có ô mới", "PENDING"), default);

        Assert.Null(f.Db.AddressChangeRequests.AsNoTracking().Single().released_contract_id);
        Assert.Equal(0, (await f.Sweep(DayAfter.AddDays(60))).Released);
        Assert.Equal("ACTIVE", f.Db.RentalContracts.AsNoTracking().Single().contract_status);
        Assert.Contains(f.Db.Notifications, n => n.user_id == 1010 && n.title == "Giữ nguyên ô cũ");
        var view = await f.Ward.GetAsync(f.Officer, "conflicts", 1, default);
        Assert.DoesNotContain("CANCEL_RELEASE", view.Actions);
    }

    [Fact]
    public async Task A_release_decision_needs_a_reason_and_is_not_offered_once_the_slot_has_gone()
    {
        using var f = await Fixture.Create();

        await Assert.ThrowsAsync<WardException>(() =>
            f.Ward.DecideAsync(f.Officer, "conflicts", 1, new("EXTEND_GRACE", " ", "PENDING"), default));
        await f.Sweep(DayAfter);
        var view = await f.Ward.GetAsync(f.Officer, "conflicts", 1, default);
        Assert.DoesNotContain("EXTEND_GRACE", view.Actions);
        await Assert.ThrowsAsync<WardException>(() =>
            f.Ward.DecideAsync(f.Officer, "conflicts", 1, new("EXTEND_GRACE", "Muộn rồi", "PENDING"), default));
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
        public MutableClock Clock { get; } = new(new DateTime(2026, 10, 9, 3, 0, 0, DateTimeKind.Utc));
        public AddressChangeRequestRepository Changes { get; private set; } = null!;
        public WardSlots Ward { get; private set; } = null!;
        public WardActor Officer { get; } = new(1, 1, "Cán bộ");

        public static async Task<Fixture> Create()
        {
            var f = new Fixture();
            await f.connection.OpenAsync();
            f.Db = new TestContext(new DbContextOptionsBuilder<StreetBizDbContext>().UseSqlite(f.connection).Options);
            await f.Db.Database.EnsureCreatedAsync();
            f.Changes = new AddressChangeRequestRepository(f.Db, f.Clock);
            var geo = new Mock<IGeolocation>();
            geo.Setup(x => x.Verify(It.IsAny<int>(), It.IsAny<GeoPoint>())).Returns(new GeofenceResult(true, 1, "test"));
            var policy = new Mock<ISidewalkPolicy>();
            policy.SetupGet(x => x.AdjacentRadiusMeters).Returns(50);
            f.Ward = new WardSlots(f.Db, geo.Object, policy.Object, f.Clock);

            f.Db.Roles.AddRange(
                new Role { role_code = "WARD_AUTHORITY", role_name = "Ward" },
                new Role { role_code = "VENDOR", role_name = "Vendor" });
            f.Db.AdministrativeUnits.AddRange(
                new AdministrativeUnit { unit_id = 1, unit_type = "WARD", unit_name = "Ward 1" },
                new AdministrativeUnit { unit_id = 2, unit_type = "WARD", unit_name = "Ward 2" });
            f.Db.UserAccounts.AddRange(
                User(1, "WARD_AUTHORITY", 1),
                User(2, "WARD_AUTHORITY", 2),
                User(1010, "VENDOR", 1),
                // Vendor 10's login is user 1010 so the test can tell user ids from vendor ids.
                User(10, "VENDOR", 1));
            f.Db.Vendors.Add(new Vendor { vendor_id = 10, user_id = 1010 });
            f.Db.PricingZones.Add(new PricingZone { zone_id = 1, ward_unit_id = 1, zone_name = "Zone", created_by = 1 });
            f.Db.BusinessRegistrations.Add(new BusinessRegistration { registration_id = 1, vendor_id = 10, ward_unit_id = 1, vendor_type = "FIXED_STOREFRONT", display_name = "Quán cũ", declared_address = "Địa chỉ cũ", registration_status = "APPROVED" });
            f.Db.SidewalkSlots.Add(new SidewalkSlot { slot_id = 1, zone_id = 1, slot_code = "OLD-SLOT", source = "WARD_DEFINED", slot_status = "ACTIVE", latitude = 10, longitude = 106 });
            f.Db.RentalApplications.Add(new RentalApplication { application_id = 1, registration_id = 1, slot_id = 1, application_method = "AUTO_ADJACENT", application_status = "APPROVED", requested_term_days = 365 });
            f.Db.RentalContracts.Add(new RentalContract { contract_id = 1, application_id = 1, slot_id = 1, vendor_id = 10, start_date = new DateOnly(2026, 1, 1), end_date = new DateOnly(2026, 12, 31), contract_status = "ACTIVE" });
            f.Db.FeeSchedules.Add(new FeeSchedule { fee_schedule_id = 1, contract_id = 1, total_amount = 100 });
            f.Db.FeeScheduleItems.Add(new FeeScheduleItem { fee_item_id = 1, fee_schedule_id = 1, due_date = new DateOnly(2026, 9, 1), amount = 100, item_status = "PAID" });
            f.Db.AddressChangeRequests.Add(new AddressChangeRequest
            {
                address_change_id = 1,
                registration_id = 1,
                new_address = "Địa chỉ mới",
                released_contract_id = 1,
                change_status = "PENDING",
                created_at = RequestedAt,
            });
            await f.Db.SaveChangesAsync();
            f.Db.ChangeTracker.Clear();
            return f;
        }

        public Task<StreetBiz.Application.Common.Models.AdjacentSlotReleaseSweepResult> Sweep(DateOnly today) =>
            new ReleaseDueAdjacentSlotsCommandHandler(
                Changes,
                new RentalContractRepository(Db),
                new RenewalRequestRepository(Db))
                .Handle(new ReleaseDueAdjacentSlotsCommand(today), default);

        public void Dispose()
        {
            Db.Dispose();
            connection.Dispose();
        }

        private static UserAccount User(long id, string role, int ward) => new()
        {
            user_id = id,
            phone_number = $"09000{id:00000}",
            password_hash = "test-only",
            full_name = $"User {id}",
            role_code = role,
            ward_unit_id = ward,
            account_status = "ACTIVE",
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
