using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using StreetBiz.Application.Common.Security;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Persistence.Repositories;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Tests;

/// <summary>
/// The database reads behind AIC-03 (duplicate registrations) and AIC-08 (period summary).
/// Ward 1 is the officer's ward, ward 2 another one.
/// </summary>
public sealed class WardInsightFactsTests
{
    private const int Ward = 1;
    private const int OtherWard = 2;
    private static readonly DateOnly From = new(2026, 9, 1);
    private static readonly DateOnly To = new(2026, 9, 30);

    // ---- AIC-03 -----------------------------------------------------------------------------

    [Fact]
    public async Task A_registration_of_another_ward_is_not_readable()
    {
        using var f = await Fixture.Create();

        Assert.Null(await f.Integrity().GetFactsAsync(OtherWard, 1, default));
        Assert.Null(await f.Integrity().GetFactsAsync(Ward, 999, default));
    }

    [Fact]
    public async Task The_same_cccd_is_found_across_vendors_and_wards_but_not_within_one_vendor()
    {
        using var f = await Fixture.Create();

        var facts = await f.Integrity().GetFactsAsync(Ward, 1, default);

        Assert.NotNull(facts);
        // R2 (vendor 11, this ward) and R3 (vendor 12, other ward). R4 shares the vendor and the
        // CCCD with R1 -- one household with several storefronts (BR-06) -- so it is not a finding.
        Assert.Equal(new long[] { 2, 3 }, facts.SameIdOtherVendors.Select(r => r.RegistrationId).OrderBy(i => i));
        var other = facts.SameIdOtherVendors.Single(r => r.RegistrationId == 3);
        Assert.Equal(OtherWard, other.WardId);
        Assert.Equal("Thanh Khê", other.WardName);
    }

    [Fact]
    public async Task The_same_address_is_found_despite_case_accents_and_punctuation()
    {
        using var f = await Fixture.Create();

        var facts = await f.Integrity().GetFactsAsync(Ward, 1, default);

        var match = Assert.Single(facts!.SameAddressOtherVendors);
        Assert.Equal(2, match.RegistrationId);
    }

    [Fact]
    public async Task A_burst_counts_the_vendor_s_registrations_around_this_one_and_applications_count_only_open_ones()
    {
        using var f = await Fixture.Create();

        var facts = await f.Integrity().GetFactsAsync(Ward, 1, default);

        // R1 (1 Sep), R4 (10 Sep) and R5 (15 Sep) by vendor 10; R7 is five months away.
        Assert.Equal(3, facts!.RegistrationsByVendorAroundThisOne);
        // Three PENDING applications; the APPROVED one that became a contract is not "open".
        Assert.Equal(3, facts.OpenApplications);
    }

    [Fact]
    public async Task Another_account_with_the_same_cccd_asking_for_the_same_slot_is_found()
    {
        using var f = await Fixture.Create();

        var facts = await f.Integrity().GetFactsAsync(Ward, 1, default);

        var contested = Assert.Single(facts!.SlotsContestedBySameId);
        Assert.Equal(2, contested.RegistrationId);
        Assert.Equal("Ô S-01", contested.Note);
    }

    [Fact]
    public async Task A_registration_without_an_id_number_finds_no_identity_matches()
    {
        using var f = await Fixture.Create();

        var facts = await f.Integrity().GetFactsAsync(Ward, 6, default);

        Assert.Empty(facts!.SameIdOtherVendors);
        Assert.Empty(facts.SlotsContestedBySameId);
    }

    // ---- AIC-08 -----------------------------------------------------------------------------

    [Fact]
    public async Task Period_facts_count_only_the_wards_violations_inside_the_period()
    {
        using var f = await Fixture.Create();

        var facts = await f.Reports().GetPeriodFactsAsync(Ward, From, To, default);

        Assert.Equal(3, facts.ViolationCount);
        // Unpaid and paid stand; the waived one is not an issued penalty.
        Assert.Equal(2, facts.PenaltiesIssued);
        Assert.Equal(1_000_000m, facts.PenaltiesIssuedAmount);
        Assert.Equal(["OUTSIDE_SLOT", "NOISE_VIOLATION"], facts.TopViolationTypes.Select(t => t.ViolationType));
        Assert.Equal(2, facts.TopViolationTypes[0].Count);
        Assert.Equal("Lấn ô", facts.TopViolationTypes[0].Label);
    }

    [Fact]
    public async Task Period_facts_judge_on_time_payment_on_the_vietnamese_calendar_day()
    {
        using var f = await Fixture.Create();

        var facts = await f.Reports().GetPeriodFactsAsync(Ward, From, To, default);

        // Two paid in September (one on its due day, one ten days late); the 30 August one is out.
        Assert.Equal(2, facts.FeeItemsPaid);
        Assert.Equal(1, facts.FeeItemsPaidOnTime);
    }

    [Fact]
    public async Task A_payment_late_in_the_evening_utc_is_still_on_time_in_da_nang()
    {
        using var f = await Fixture.Create();
        // Due 10 Sep; paid 10 Sep 23:30 Đà Nẵng = 16:30 UTC the same day, so still on time, and
        // paid 11 Sep 00:30 Đà Nẵng = 10 Sep 17:30 UTC, which is a UTC date that looks on time but is late.
        f.Db.FeeScheduleItems.AddRange(
            Fixture.Item(20, 1, new DateOnly(2026, 9, 10), 100_000m, FeeItemStatuses.Paid,
                new DateTime(2026, 9, 10, 16, 30, 0, DateTimeKind.Utc)),
            Fixture.Item(21, 1, new DateOnly(2026, 9, 10), 100_000m, FeeItemStatuses.Paid,
                new DateTime(2026, 9, 10, 17, 30, 0, DateTimeKind.Utc)));
        await f.Db.SaveChangesAsync();

        var facts = await f.Reports().GetPeriodFactsAsync(Ward, From, To, default);

        Assert.Equal(4, facts.FeeItemsPaid);
        Assert.Equal(2, facts.FeeItemsPaidOnTime);
    }

    [Fact]
    public async Task Overdue_debt_names_the_largest_debtor_and_counts_households()
    {
        using var f = await Fixture.Create();

        var facts = await f.Reports().GetPeriodFactsAsync(Ward, From, To, default);

        Assert.Equal(3, facts.OverdueItemCount);
        Assert.Equal(2, facts.OverdueVendorCount);
        Assert.NotNull(facts.LargestDebtor);
        Assert.Equal("Chủ hộ A", facts.LargestDebtor.VendorName);
        Assert.Equal(1_200_000m, facts.LargestDebtor.Amount);
    }

    [Fact]
    public async Task A_superseded_schedule_s_overdue_items_are_not_debt()
    {
        using var f = await Fixture.Create();
        var schedule = await f.Db.FeeSchedules.SingleAsync(s => s.fee_schedule_id == 1);
        schedule.superseded_at = new DateTime(2026, 9, 25, 0, 0, 0, DateTimeKind.Utc);
        await f.Db.SaveChangesAsync();

        var facts = await f.Reports().GetPeriodFactsAsync(Ward, From, To, default);

        // Only vendor B's 500 000 on the live schedule remains.
        Assert.Equal(1, facts.OverdueItemCount);
        Assert.Equal(500_000m, facts.LargestDebtor!.Amount);
    }

    // ---- fixture ----------------------------------------------------------------------------

    private sealed class Fixture : IDisposable
    {
        private SqliteConnection Connection { get; } = new("Data Source=:memory:");
        public TestContext Db { get; private set; } = null!;

        public RegistrationIntegrityReader Integrity() => new(new TestContext(Options()));

        public WardReportRepository Reports() => new(new TestContext(Options()));

        private DbContextOptions<StreetBizDbContext> Options() =>
            new DbContextOptionsBuilder<StreetBizDbContext>().UseSqlite(Connection).Options;

        public static async Task<Fixture> Create()
        {
            var f = new Fixture();
            await f.Connection.OpenAsync();
            f.Db = new TestContext(f.Options());
            await f.Db.Database.EnsureCreatedAsync();

            f.Db.Roles.AddRange(
                new Role { role_code = "WARD_AUTHORITY", role_name = "Ward" },
                new Role { role_code = "VENDOR", role_name = "Vendor" });
            f.Db.AdministrativeUnits.AddRange(
                new AdministrativeUnit { unit_id = Ward, unit_type = "WARD", unit_name = "Hải Châu" },
                new AdministrativeUnit { unit_id = OtherWard, unit_type = "WARD", unit_name = "Thanh Khê" });
            f.Db.UserAccounts.AddRange(
                User(1, "0900000001", "Cán bộ", "WARD_AUTHORITY", Ward),
                User(10, "0900000010", "Chủ hộ A", "VENDOR", null),
                User(11, "0900000011", "Chủ hộ B", "VENDOR", null),
                User(12, "0900000012", "Chủ hộ C", "VENDOR", null));
            f.Db.Vendors.AddRange(
                new Vendor { vendor_id = 10, user_id = 10 },
                new Vendor { vendor_id = 11, user_id = 11 },
                new Vendor { vendor_id = 12, user_id = 12 });

            const string idNumber = "048185000001";
            f.Db.BusinessRegistrations.AddRange(
                Registration(1, 10, Ward, "Hộ 1", "APPROVED", idNumber, "12 Lê Duẩn, Hải Châu", new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)),
                Registration(2, 11, Ward, "Hộ 2", "UNDER_REVIEW", idNumber, "12 le duan - HAI CHAU", new DateTime(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc)),
                Registration(3, 12, OtherWard, "Hộ 3", "SUBMITTED", idNumber, "7 Điện Biên Phủ", new DateTime(2026, 9, 4, 0, 0, 0, DateTimeKind.Utc)),
                // Same vendor and same CCCD as R1: a second storefront of one household.
                Registration(4, 10, Ward, "Hộ 4", "APPROVED", idNumber, "99 Trần Phú", new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc)),
                Registration(5, 10, Ward, "Hộ 5", "SUBMITTED", null, "5 Nguyễn Văn Linh", new DateTime(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc)),
                Registration(6, 11, Ward, "Hộ 6", "DRAFT", null, null, new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc)),
                // Five months away from the others: outside the burst window.
                Registration(7, 10, Ward, "Hộ 7", "APPROVED", "999", "1 Hoàng Diệu", new DateTime(2027, 2, 1, 0, 0, 0, DateTimeKind.Utc)));

            f.Db.PricingZones.AddRange(
                new PricingZone { zone_id = 1, ward_unit_id = Ward, ward_unit_type = "WARD", zone_name = "Zone 1", price_per_day = 30_000, created_by = 1 },
                new PricingZone { zone_id = 2, ward_unit_id = OtherWard, ward_unit_type = "WARD", zone_name = "Zone 2", price_per_day = 30_000, created_by = 1 });
            f.Db.SidewalkSlots.AddRange(
                Slot(1, 1), Slot(2, 1), Slot(3, 1), Slot(4, 1), Slot(5, 1));

            // R1 has three open applications and one that became a contract; R2 is asking for
            // R1's first slot; R6 asks for R1's second slot but is a different person.
            f.Db.RentalApplications.AddRange(
                Application(1, 1, 1, ApplicationStatuses.Pending),
                Application(2, 1, 2, ApplicationStatuses.Pending),
                Application(3, 1, 3, ApplicationStatuses.UnderReview),
                Application(4, 2, 1, ApplicationStatuses.Pending),
                Application(5, 6, 2, ApplicationStatuses.Pending),
                Application(11, 1, 4, ApplicationStatuses.Approved),
                Application(12, 2, 5, ApplicationStatuses.Approved));

            f.Db.RentalContracts.AddRange(
                new RentalContract
                {
                    contract_id = 1, application_id = 11, slot_id = 4, vendor_id = 10,
                    start_date = new DateOnly(2026, 8, 1), end_date = new DateOnly(2026, 10, 29), contract_status = "ACTIVE",
                },
                new RentalContract
                {
                    contract_id = 2, application_id = 12, slot_id = 5, vendor_id = 11,
                    start_date = new DateOnly(2026, 8, 1), end_date = new DateOnly(2026, 10, 29), contract_status = "ACTIVE",
                });

            var generated = new DateTime(2026, 8, 1, 3, 0, 0, DateTimeKind.Utc);
            f.Db.FeeSchedules.AddRange(
                new FeeSchedule { fee_schedule_id = 1, contract_id = 1, revision = 1, total_amount = 5_000_000m, generated_at = generated },
                new FeeSchedule { fee_schedule_id = 2, contract_id = 2, revision = 1, total_amount = 2_000_000m, generated_at = generated });
            f.Db.FeeScheduleItems.AddRange(
                Item(1, 1, new DateOnly(2026, 9, 10), 1_000_000m, FeeItemStatuses.Paid, new DateTime(2026, 9, 9, 3, 0, 0, DateTimeKind.Utc)),
                Item(2, 1, new DateOnly(2026, 9, 10), 1_000_000m, FeeItemStatuses.Paid, new DateTime(2026, 9, 20, 3, 0, 0, DateTimeKind.Utc)),
                Item(3, 1, new DateOnly(2026, 9, 20), 900_000m, FeeItemStatuses.Overdue, null),
                Item(4, 1, new DateOnly(2026, 9, 25), 300_000m, FeeItemStatuses.Overdue, null),
                Item(5, 2, new DateOnly(2026, 9, 5), 500_000m, FeeItemStatuses.Overdue, null),
                Item(6, 2, new DateOnly(2026, 8, 25), 700_000m, FeeItemStatuses.Paid, new DateTime(2026, 8, 30, 3, 0, 0, DateTimeKind.Utc)));

            f.Db.ViolationTypes.AddRange(
                new ViolationType { violation_type_code = "OUTSIDE_SLOT", description = "Lấn ô", is_active = true },
                new ViolationType { violation_type_code = "NOISE_VIOLATION", description = "Gây ồn", is_active = true });
            f.Db.PenaltyFeeSchedules.AddRange(
                new PenaltyFeeSchedule { penalty_schedule_id = 1, ward_unit_id = Ward, violation_type = "OUTSIDE_SLOT", penalty_amount = 500_000m, created_by = 1 },
                new PenaltyFeeSchedule { penalty_schedule_id = 2, ward_unit_id = Ward, violation_type = "NOISE_VIOLATION", penalty_amount = 300_000m, created_by = 1 });
            f.Db.Violations.AddRange(
                Violation(1, 4, 1, 10, "OUTSIDE_SLOT", new DateTime(2026, 9, 12, 3, 0, 0, DateTimeKind.Utc)),
                Violation(2, 4, 1, 10, "OUTSIDE_SLOT", new DateTime(2026, 9, 15, 3, 0, 0, DateTimeKind.Utc)),
                Violation(3, 5, 2, 11, "NOISE_VIOLATION", new DateTime(2026, 9, 20, 3, 0, 0, DateTimeKind.Utc)),
                // Outside the period.
                Violation(4, 4, 1, 10, "OUTSIDE_SLOT", new DateTime(2026, 8, 20, 3, 0, 0, DateTimeKind.Utc)));
            f.Db.Penalties.AddRange(
                new Penalty
                {
                    penalty_id = 1, violation_id = 1, penalty_schedule_id = 1, amount = 500_000m,
                    penalty_status = PenaltyStatuses.Unpaid, created_at = generated,
                },
                new Penalty
                {
                    penalty_id = 2, violation_id = 2, penalty_schedule_id = 1, amount = 500_000m,
                    penalty_status = PenaltyStatuses.Paid, created_at = generated,
                    paid_at = new DateTime(2026, 9, 16, 3, 0, 0, DateTimeKind.Utc),
                },
                new Penalty
                {
                    penalty_id = 3, violation_id = 3, penalty_schedule_id = 2, amount = 300_000m,
                    penalty_status = PenaltyStatuses.Waived, created_at = generated,
                    waived_by = 1, waiver_reason = "Khắc phục ngay", waived_at = generated,
                });

            await f.Db.SaveChangesAsync();
            f.Db.ChangeTracker.Clear();
            return f;
        }

        private static UserAccount User(long id, string phone, string name, string role, int? ward) => new()
        {
            user_id = id, phone_number = phone, password_hash = "x", full_name = name, role_code = role,
            ward_unit_id = ward, account_status = "ACTIVE",
        };

        private static BusinessRegistration Registration(
            long id, long vendor, int ward, string name, string status, string? idNumber, string? address, DateTime createdAt) => new()
        {
            registration_id = id, vendor_id = vendor, ward_unit_id = ward, vendor_type = "FIXED_STOREFRONT",
            display_name = name, registration_status = status, id_number = idNumber, declared_address = address,
            created_at = createdAt,
        };

        private static SidewalkSlot Slot(long id, int zone) => new()
        {
            slot_id = id, zone_id = zone, slot_code = $"S-{id:00}", latitude = 16.06m, longitude = 108.21m,
            slot_status = "ACTIVE", source = "WARD_DEFINED",
        };

        private static RentalApplication Application(long id, long registration, long slot, string status) => new()
        {
            application_id = id, registration_id = registration, slot_id = slot,
            application_method = "MANUAL_SELECTED", requested_term_days = 90, application_status = status,
        };

        private static Violation Violation(long id, long slot, long contract, long vendor, string type, DateTime at) => new()
        {
            violation_id = id, slot_id = slot, contract_id = contract, vendor_id = vendor, violation_type = type,
            recorded_by = 1, source = "ON_SITE", recorded_at = at,
        };

        public static FeeScheduleItem Item(
            long id, long scheduleId, DateOnly due, decimal amount, string status, DateTime? paidAt) => new()
        {
            fee_item_id = id, fee_schedule_id = scheduleId, due_date = due, amount = amount, item_status = status,
            paid_at = paidAt,
        };

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
