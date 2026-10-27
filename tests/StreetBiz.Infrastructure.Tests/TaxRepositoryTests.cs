using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Models;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Persistence.Repositories;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Tests;

/// <summary>
/// The household tax repository on SQLite: Vietnamese calendar days, refunds, what each caller can
/// reach, and the referral/announcement/notification bookkeeping.
/// </summary>
public sealed class TaxRepositoryTests
{
    private static readonly DateTime Now = new(2026, 10, 18, 3, 0, 0, DateTimeKind.Utc); // 10:00 in Đà Nẵng
    private static readonly DateOnly Today = new(2026, 10, 18);

    private const long AdminUser = 1;
    private const long OfficerWard10 = 2;
    private const long OfficerWard11 = 3;
    private const long Vendor1 = 1; // user 5, ward 10, has a tax code and shares revenue
    private const long Vendor2 = 2; // user 6, ward 10, no tax code, contract expired
    private const long Vendor3 = 3; // user 7, ward 11

    private static NewTaxPolicy NewPolicy(int year = 2026, decimal threshold = 1_000_000_000m) => new(
        year, "Nghị định 141/2026/NĐ-CP", threshold, 1_000_000_000m, 3_000_000_000m, 50_000_000_000m, true,
        0.15m, 0.17m, 0.20m, 1, 31, 0.8m, "ghi chú",
        [
            new TaxIndustryRateRow("FOOD_SERVICE", "Ăn uống", 0.03m, 0.015m, null),
            new TaxIndustryRateRow("OTHER", "Khác", 0.02m, 0.01m, "ngành khác"),
        ]);

    // ------------------------------------------------------------------------------ policies

    [Fact]
    public async Task The_highest_version_of_a_year_is_the_one_in_force_and_earlier_years_are_inherited()
    {
        using var f = await Fixture.Create();
        var repository = f.Repository;
        var v1 = await repository.CreatePolicyAsync(AdminUser, NewPolicy(2026, 500_000_000m), Now, default);
        var v2 = await repository.CreatePolicyAsync(AdminUser, NewPolicy(2026, 1_000_000_000m), Now.AddDays(1), default);

        Assert.Equal(1, v1.VersionNo);
        Assert.Equal(2, v2.VersionNo);
        Assert.Equal(1_000_000_000m, (await repository.GetPolicyAsync(2026, default))!.RevenueThreshold);
        // Nobody published 2027: it inherits the last known rules rather than having none.
        var inherited = await repository.GetPolicyAsync(2027, default);
        Assert.Equal(2026, inherited!.TaxYear);
        Assert.Equal(2, inherited.VersionNo);
    }

    [Fact]
    public async Task A_year_before_any_published_policy_has_none()
    {
        using var f = await Fixture.Create();
        await f.Repository.CreatePolicyAsync(AdminUser, NewPolicy(2027), Now, default);

        Assert.Null(await f.Repository.GetPolicyAsync(2026, default));
    }

    [Fact]
    public async Task A_published_policy_keeps_its_rates_and_leaves_an_audit_row()
    {
        using var f = await Fixture.Create();

        var policy = await f.Repository.CreatePolicyAsync(AdminUser, NewPolicy(), Now, default);
        f.Db.ChangeTracker.Clear();
        var read = await f.Repository.GetPolicyAsync(2026, default);

        Assert.Equal(["FOOD_SERVICE", "OTHER"], read!.Rates.Select(r => r.Code));
        Assert.Equal(0.015m, read.Rates[0].PitRate);
        Assert.Equal("ngành khác", read.Rates[1].Note);
        Assert.Equal(1, read.AnnualNoticeMonth);
        Assert.Equal(31, read.AnnualNoticeDay);
        var audit = await f.Db.AuditLogs.SingleAsync(row => row.action == "TAX_POLICY_PUBLISHED");
        Assert.Equal(policy.PolicyId, audit.entity_id);
        Assert.Equal(AdminUser, audit.actor_user_id);
    }

    [Fact]
    public async Task Policies_are_listed_newest_first()
    {
        using var f = await Fixture.Create();
        await f.Repository.CreatePolicyAsync(AdminUser, NewPolicy(2026), Now, default);
        await f.Repository.CreatePolicyAsync(AdminUser, NewPolicy(2026), Now, default);
        await f.Repository.CreatePolicyAsync(AdminUser, NewPolicy(2027), Now, default);

        var list = await f.Repository.ListPoliciesAsync(default);

        Assert.Equal([(2027, 1), (2026, 2), (2026, 1)], list.Select(p => (p.TaxYear, p.VersionNo)));
    }

    // -------------------------------------------------------------------------------- profile

    [Fact]
    public async Task Changing_what_the_ward_may_see_is_audited_and_saving_the_same_choice_is_not()
    {
        using var f = await Fixture.Create();
        var repository = f.Repository;

        var created = await repository.SaveProfileAsync(Vendor2, 6, "0123456789", "FOOD_SERVICE", false, true, Now, default);
        await repository.SaveProfileAsync(Vendor2, 6, "0123456789", "GOODS", true, true, Now.AddHours(1), default);
        var withdrawn = await repository.SaveProfileAsync(Vendor2, 6, "0123456789", "GOODS", true, false, Now.AddHours(2), default);

        Assert.True(created.ShareRevenueWithWard);
        Assert.Equal(Now, created.ConsentUpdatedAt);
        Assert.False(withdrawn.ShareRevenueWithWard);
        Assert.Equal(Now.AddHours(2), withdrawn.ConsentUpdatedAt);
        Assert.Equal("GOODS", withdrawn.IndustryCode);
        var actions = await f.Db.AuditLogs.AsNoTracking()
            .Where(row => row.entity_type == "VendorTaxProfile").OrderBy(row => row.audit_id).Select(row => row.action).ToListAsync();
        Assert.Equal(["TAX_REVENUE_SHARING_ON", "TAX_REVENUE_SHARING_OFF"], actions);
    }

    [Fact]
    public async Task A_new_profile_that_shares_nothing_leaves_no_consent_trail()
    {
        using var f = await Fixture.Create();

        var profile = await f.Repository.SaveProfileAsync(Vendor2, 6, null, "FOOD_SERVICE", false, false, Now, default);

        Assert.False(profile.ShareRevenueWithWard);
        Assert.Null(profile.ConsentUpdatedAt);
        Assert.Empty(f.Db.AuditLogs);
    }

    [Fact]
    public async Task The_identity_is_the_owner_and_the_approved_business_names()
    {
        using var f = await Fixture.Create();

        var (owner, business) = await f.Repository.GetVendorIdentityAsync(Vendor1, default);

        Assert.Equal("Phạm Thị Lan", owner);
        Assert.Equal("Bún chả Hải Châu · Bánh mì Cô Lan", business);
    }

    // ---------------------------------------------------------------------------- revenue book

    private static async Task AddRevenueScenario(Fixture f)
    {
        // Vendor 1's storefront is 1, vendor 2's is 2. 17:00 UTC is midnight in Đà Nẵng.
        f.Db.Orders.AddRange(
            Order(1, 1, "COMPLETED", 100_000m, new DateTime(2026, 10, 17, 16, 59, 0, DateTimeKind.Utc)), // 17 Oct 23:59 local
            Order(2, 1, "COMPLETED", 200_000m, new DateTime(2026, 10, 17, 17, 0, 0, DateTimeKind.Utc)),  // 18 Oct 00:00 local
            Order(3, 1, "CANCELLED", 999_000m, null),
            Order(4, 2, "COMPLETED", 70_000m, new DateTime(2026, 10, 12, 5, 0, 0, DateTimeKind.Utc)),    // another household
            Order(5, 1, "COMPLETED", 40_000m, new DateTime(2026, 9, 30, 10, 0, 0, DateTimeKind.Utc)));
        f.Db.PaymentTransactions.Add(new PaymentTransaction
        {
            transaction_id = 1, order_id = 2, idempotency_key = "PAY-2", payment_purpose = "ORDER", provider = "MOMO",
            amount = 200_000m, transaction_status = "SUCCESS",
        });
        f.Db.RefundTransactions.AddRange(
            Refund(1, 50_000m, "SUCCESS"),
            Refund(2, 30_000m, "PENDING"));
        f.Db.RevenueEntries.AddRange(
            Entry(Vendor1, new DateOnly(2026, 10, 18), 300_000m, "CASH", null),
            Entry(Vendor1, new DateOnly(2026, 10, 18), 20_000m, "BANK_TRANSFER", null),
            Entry(Vendor1, new DateOnly(2026, 10, 17), 500_000m, "CASH", Now.AddHours(-2)), // voided
            Entry(Vendor1, new DateOnly(2026, 10, 10), 1_000_000m, "CASH", null),
            Entry(Vendor2, new DateOnly(2026, 10, 18), 7_000m, "CASH", null));
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();
    }

    private static Order Order(long id, long storefrontId, string status, decimal total, DateTime? completedAt) => new()
    {
        order_id = id, order_code = $"DH-{id}", customer_user_id = 9, storefront_id = storefrontId, order_status = status,
        subtotal_amount = total, total_amount = total, completed_at = completedAt, placed_at = completedAt, created_at = Now.AddDays(-30),
    };

    private static RefundTransaction Refund(long id, decimal amount, string status) => new()
    {
        refund_id = id, order_id = 2, payment_transaction_id = 1, idempotency_key = $"REF-{id}", amount = amount,
        refund_reason = "COMPLAINT_RESOLVED", provider = "MOMO", refund_status = status, requested_at = Now,
    };

    private static RevenueEntry Entry(long vendorId, DateOnly date, decimal amount, string channel, DateTime? voidedAt) => new()
    {
        vendor_id = vendorId, entry_date = date, amount = amount, channel = channel, created_at = Now.AddDays(-1),
        voided_at = voidedAt, void_reason = voidedAt is null ? null : "Nhập nhầm",
    };

    [Fact]
    public async Task Revenue_is_counted_on_the_Vietnamese_day_net_of_refunds_and_without_voided_entries()
    {
        using var f = await Fixture.Create();
        await AddRevenueScenario(f);

        var days = await f.Repository.GetDailyRevenueAsync(Vendor1, new DateOnly(2026, 10, 1), Today, default);

        Assert.Equal(
            [new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 17), new DateOnly(2026, 10, 18)],
            days.Select(day => day.Date));
        var tenth = days[0];
        Assert.Equal((0m, 0, 1_000_000m), (tenth.Online, tenth.OnlineOrders, tenth.Recorded));
        var seventeenth = days[1]; // order 1 only: 23:59 local; the voided 500.000 is not counted
        Assert.Equal((100_000m, 1, 0m), (seventeenth.Online, seventeenth.OnlineOrders, seventeenth.Recorded));
        var eighteenth = days[2]; // order 2 at local midnight, 200.000 less the 50.000 refund (the pending 30.000 does not count yet)
        Assert.Equal((150_000m, 1, 320_000m), (eighteenth.Online, eighteenth.OnlineOrders, eighteenth.Recorded));
        Assert.Equal(470_000m, eighteenth.Total);
    }

    [Fact]
    public async Task A_range_is_inclusive_of_both_ends_and_excludes_other_households()
    {
        using var f = await Fixture.Create();
        await AddRevenueScenario(f);

        var oneDay = await f.Repository.GetDailyRevenueAsync(Vendor1, Today, Today, default);
        var other = await f.Repository.GetDailyRevenueAsync(Vendor2, new DateOnly(2026, 10, 1), Today, default);

        Assert.Equal([Today], oneDay.Select(day => day.Date));
        Assert.Equal([new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 18)], other.Select(day => day.Date));
        Assert.Equal(70_000m, other[0].Online);
        Assert.Equal(7_000m, other[1].Recorded);
    }

    [Fact]
    public async Task Entries_come_back_oldest_first_with_the_voided_ones_marked()
    {
        using var f = await Fixture.Create();
        await AddRevenueScenario(f);

        var entries = await f.Repository.ListEntriesAsync(Vendor1, new DateOnly(2026, 10, 1), Today, default);

        Assert.Equal(4, entries.Count);
        Assert.Equal([new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 17), new DateOnly(2026, 10, 18), new DateOnly(2026, 10, 18)],
            entries.Select(entry => entry.Date));
        Assert.NotNull(entries[1].VoidedAt);
        Assert.Equal("Nhập nhầm", entries[1].VoidReason);
        Assert.Null(entries[0].VoidedAt);
    }

    [Fact]
    public async Task Moments_read_back_from_the_database_are_marked_as_UTC()
    {
        // SQL Server returns DATETIME2 as Unspecified; a client would read that as local time.
        using var f = await Fixture.Create();
        await AddReferralScenario(f);
        await f.Repository.CreatePolicyAsync(AdminUser, NewPolicy(), Now, default);
        var entry = await f.Repository.AddEntryAsync(Vendor1, new DateOnly(2026, 10, 18), 100_000m, "CASH", null, Now, default);
        await f.Repository.VoidEntryAsync(Vendor1, 5, entry.EntryId, "x", Now, default);
        await f.Repository.SaveDeclarationAsync(Vendor1, 5, Filing(), Now, default);
        await f.Repository.CreateAnnouncementAsync(10, OfficerWard10, "ALL", 2026, "T", "B", [5], Now, default);
        await f.Repository.RefreshReferralsAsync(10, Now, 90, default);
        f.Db.ChangeTracker.Clear();

        var entries = await f.Repository.ListEntriesAsync(Vendor1, new DateOnly(2026, 10, 1), Today, default);
        var profile = await f.Repository.GetProfileAsync(Vendor1, default);
        var declaration = (await f.Repository.ListDeclarationsAsync(Vendor1, 2026, default)).Single();
        var referral = (await f.Repository.ListReferralsAsync(10, null, default)).First();
        var announcement = (await f.Repository.ListAnnouncementsAsync(10, default)).Single();
        var household = (await f.Repository.ListWardHouseholdsAsync(10, Today, default)).First(h => h.VendorId == Vendor1);

        Assert.All(entries, e => Assert.Equal(DateTimeKind.Utc, e.CreatedAt.Kind));
        Assert.Equal(DateTimeKind.Utc, entries.Single(e => e.VoidedAt is not null).VoidedAt!.Value.Kind);
        Assert.Equal(DateTimeKind.Utc, profile!.ConsentUpdatedAt!.Value.Kind);
        Assert.Equal(DateTimeKind.Utc, declaration.CreatedAt.Kind);
        Assert.Equal(DateTimeKind.Utc, referral.DetectedAt.Kind);
        Assert.Equal(DateTimeKind.Utc, referral.CreatedAt.Kind);
        Assert.Equal(DateTimeKind.Utc, announcement.CreatedAt.Kind);
        Assert.Equal(DateTimeKind.Utc, household.ApprovedAt.Kind);
        Assert.Equal(DateTimeKind.Utc, (await f.Repository.GetPolicyAsync(2026, default))!.CreatedAt.Kind);
    }

    [Fact]
    public async Task An_entry_is_added_and_voided_only_by_its_own_household()
    {
        using var f = await Fixture.Create();
        var repository = f.Repository;
        var entry = await repository.AddEntryAsync(Vendor1, new DateOnly(2026, 10, 18), 2_500_000m, "CASH", "Cả ngày", Now, default);

        Assert.Null(await repository.VoidEntryAsync(Vendor2, 6, entry.EntryId, "x", Now, default));
        var voided = await repository.VoidEntryAsync(Vendor1, 5, entry.EntryId, "Nhập nhầm", Now.AddMinutes(5), default);

        Assert.NotNull(voided!.VoidedAt);
        Assert.Equal("Nhập nhầm", voided.VoidReason);
        await Assert.ThrowsAsync<ConflictException>(() => repository.VoidEntryAsync(Vendor1, 5, entry.EntryId, null, Now, default));
        var audit = await f.Db.AuditLogs.SingleAsync(row => row.action == "REVENUE_ENTRY_VOIDED");
        Assert.Equal(entry.EntryId, audit.entity_id);
        Assert.Equal(5, audit.actor_user_id);
        // Voiding never removes the row: the book keeps its history.
        Assert.Equal(1, await f.Db.RevenueEntries.CountAsync());
    }

    [Fact]
    public async Task Year_revenue_for_many_households_stops_at_the_given_day_and_remembers_the_first_sale()
    {
        using var f = await Fixture.Create();
        await AddRevenueScenario(f);

        var through17th = await f.Repository.GetYearRevenueAsync([Vendor1, Vendor2, Vendor3], 2026, new DateOnly(2026, 10, 17), default);
        var wholeYear = await f.Repository.GetYearRevenueAsync([Vendor1, Vendor2, Vendor3], 2026, null, default);

        var one = through17th.Single(row => row.VendorId == Vendor1);
        Assert.Equal((140_000m, 1_000_000m), (one.Online, one.Recorded)); // orders 1 and 5, the 10 Oct entry; not the 18th
        Assert.Equal(new DateOnly(2026, 9, 30), one.FirstDate); // order 5, 17:00 local on 30 September
        var two = through17th.Single(row => row.VendorId == Vendor2);
        Assert.Equal((70_000m, 0m), (two.Online, two.Recorded));
        Assert.Equal(new DateOnly(2026, 10, 12), two.FirstDate);
        Assert.DoesNotContain(through17th, row => row.VendorId == Vendor3);

        var full = wholeYear.Single(row => row.VendorId == Vendor1);
        Assert.Equal((290_000m, 1_320_000m), (full.Online, full.Recorded));
        Assert.Equal(1_610_000m, full.Total);
    }

    [Fact]
    public async Task A_year_with_nothing_in_it_has_no_rows()
    {
        using var f = await Fixture.Create();
        await AddRevenueScenario(f);

        Assert.Empty(await f.Repository.GetYearRevenueAsync([Vendor1], 2027, null, default));
        Assert.Empty(await f.Repository.GetYearRevenueAsync([], 2026, null, default));
        Assert.Empty(await f.Repository.GetYearRevenueAsync([Vendor1], 2026, new DateOnly(2025, 12, 31), default));
    }

    // ------------------------------------------------------------------------------- filings

    private static SaveTaxDeclaration Filing(decimal declared = 900_000_000m, string? reference = "BN-1") => new(
        2026, "YEAR", 0, declared, 850_000_000m, new DateOnly(2027, 1, 20), reference, null, null);

    [Fact]
    public async Task A_filing_is_confirmed_updated_and_retracted_with_an_audit_trail()
    {
        using var f = await Fixture.Create();
        var repository = f.Repository;

        var first = await repository.SaveDeclarationAsync(Vendor1, 5, Filing(), Now, default);
        var again = await repository.SaveDeclarationAsync(Vendor1, 5, Filing(950_000_000m, "BN-2"), Now.AddDays(1), default);

        Assert.Equal(first.DeclarationId, again.DeclarationId);
        Assert.Equal(Now, again.CreatedAt);
        Assert.Equal(Now.AddDays(1), again.UpdatedAt);
        Assert.Equal(950_000_000m, again.RevenueDeclared);
        Assert.Equal(850_000_000m, again.RevenueSystem);
        Assert.Single(await repository.ListDeclarationsAsync(Vendor1, 2026, default));
        Assert.Empty(await repository.ListDeclarationsAsync(Vendor2, 2026, default));

        Assert.False(await repository.DeleteDeclarationAsync(Vendor2, 6, 2026, "YEAR", 0, Now, default));
        Assert.True(await repository.DeleteDeclarationAsync(Vendor1, 5, 2026, "YEAR", 0, Now, default));
        Assert.Empty(await repository.ListDeclarationsAsync(Vendor1, 2026, default));
        Assert.Equal(
            ["TAX_FILING_CONFIRMED", "TAX_FILING_UPDATED", "TAX_FILING_RETRACTED"],
            await f.Db.AuditLogs.AsNoTracking().OrderBy(row => row.audit_id).Select(row => row.action).ToListAsync());
    }

    [Fact]
    public async Task Filed_households_are_listed_per_period()
    {
        using var f = await Fixture.Create();
        await f.Repository.SaveDeclarationAsync(Vendor1, 5, Filing(), Now, default);
        await f.Repository.SaveDeclarationAsync(Vendor2, 6, Filing() with { PeriodType = "QUARTER", PeriodNo = 3 }, Now, default);

        var year = await f.Repository.ListFiledVendorsAsync([Vendor1, Vendor2, Vendor3], 2026, "YEAR", 0, default);
        var quarter = await f.Repository.ListFiledVendorsAsync([Vendor1, Vendor2], 2026, "QUARTER", 3, default);

        Assert.Equal([Vendor1], year);
        Assert.Equal([Vendor2], quarter);
        Assert.Empty(await f.Repository.ListFiledVendorsAsync([], 2026, "YEAR", 0, default));
    }

    // ----------------------------------------------------------------------------- households

    [Fact]
    public async Task The_ward_sees_its_own_households_with_the_facts_their_status_comes_from()
    {
        using var f = await Fixture.Create();
        f.Db.PermitScanLogs.AddRange(
            Scan(1, 1, "VALID", OfficerWard10, Now.AddDays(-9), "WARD_INSPECTION"),
            Scan(2, 1, "VALID", OfficerWard10, Now.AddDays(-2), "WARD_INSPECTION"),
            Scan(3, 1, "VALID", null, Now.AddDays(-1), "PUBLIC_CHECK")); // a customer's scan is not an inspection
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();

        var households = await f.Repository.ListWardHouseholdsAsync(10, Today, default);

        Assert.Equal([Vendor1, Vendor2], households.Select(h => h.VendorId).Order());
        var one = households.Single(h => h.VendorId == Vendor1);
        Assert.Equal("Bún chả Hải Châu · Bánh mì Cô Lan", one.DisplayName); // both approved registrations, earliest first
        Assert.Equal(Now.AddDays(-200), one.ApprovedAt);
        Assert.Equal("Phạm Thị Lan", one.OwnerName);
        Assert.Equal("123 Nguyễn Văn Linh", one.Address);
        Assert.Equal(1, one.TotalContracts);
        Assert.Equal(1, one.ActiveContracts);
        Assert.Equal(["NVL-01"], one.SlotCodes);
        Assert.Equal("ACTIVE", one.PermitStatus);
        Assert.Equal(Now.AddDays(-2), one.LastInspectionAt);
        Assert.Equal("048090001234", one.TaxCode);
        Assert.True(one.ShareRevenueWithWard);

        var two = households.Single(h => h.VendorId == Vendor2);
        Assert.Equal(1, two.TotalContracts);
        Assert.Equal(0, two.ActiveContracts);
        Assert.Empty(two.SlotCodes);
        Assert.Null(two.PermitStatus);
        Assert.Null(two.LastInspectionAt);
        Assert.Null(two.TaxCode);
        Assert.False(two.ShareRevenueWithWard);
        Assert.Equal("FIXED_STOREFRONT", one.VendorType);
        Assert.Equal("ITINERANT", two.VendorType);
    }

    [Fact]
    public async Task Another_wards_households_are_not_listed()
    {
        using var f = await Fixture.Create();

        var ward11 = await f.Repository.ListWardHouseholdsAsync(11, Today, default);

        Assert.Equal([Vendor3], ward11.Select(h => h.VendorId));
        Assert.Empty(await f.Repository.ListWardHouseholdsAsync(99, Today, default));
    }

    [Fact]
    public async Task A_household_that_is_not_approved_is_not_a_household_yet()
    {
        using var f = await Fixture.Create();
        f.Db.BusinessRegistrations.Add(new BusinessRegistration
        {
            registration_id = 9, vendor_id = 4, ward_unit_id = 10, vendor_type = "ITINERANT", display_name = "Chờ duyệt",
            registration_status = "SUBMITTED", created_at = Now,
        });
        f.Db.UserAccounts.Add(User(8, "VENDOR", 10, "Vũ Minh Khôi"));
        f.Db.Vendors.Add(new Vendor { vendor_id = 4, user_id = 8 });
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();

        var households = await f.Repository.ListWardHouseholdsAsync(10, Today, default);

        Assert.DoesNotContain(households, h => h.VendorId == 4);
    }

    // ---------------------------------------------------------------------------- referrals

    private static async Task AddReferralScenario(Fixture f)
    {
        f.Db.Violations.AddRange(
            Violation(1, "NO_PERMIT", contractId: null, slotId: 1, vendorId: null, recordedBy: OfficerWard10, Now.AddDays(-3)),
            Violation(2, "EXPIRED_OR_INVALID_PERMIT", contractId: 1, slotId: 1, vendorId: Vendor1, recordedBy: OfficerWard10, Now.AddDays(-5)),
            Violation(3, "OUTSIDE_SLOT", contractId: 1, slotId: 1, vendorId: Vendor1, recordedBy: OfficerWard10, Now.AddDays(-4)),
            Violation(4, "NO_PERMIT", contractId: null, slotId: 1, vendorId: null, recordedBy: OfficerWard10, Now.AddDays(-200)),
            Violation(5, "NO_PERMIT", contractId: null, slotId: null, vendorId: null, recordedBy: OfficerWard11, Now.AddDays(-2)));
        f.Db.PermitScanLogs.AddRange(
            Scan(1, null, "NOT_FOUND", OfficerWard10, Now.AddDays(-2), "WARD_INSPECTION"),
            Scan(2, 1, "REVOKED", OfficerWard10, Now.AddDays(-1), "WARD_INSPECTION"),
            Scan(3, 1, "VALID", OfficerWard10, Now.AddDays(-1), "WARD_INSPECTION"),
            Scan(4, null, "NOT_FOUND", null, Now.AddDays(-1), "PUBLIC_CHECK"));
        f.Db.AddressChangeRequests.AddRange(
            new AddressChangeRequest { address_change_id = 1, registration_id = 1, new_address = "200 Hoàng Diệu", change_status = "APPROVED", reviewed_by = OfficerWard10, reviewed_at = Now.AddDays(-4), created_at = Now.AddDays(-6) },
            new AddressChangeRequest { address_change_id = 2, registration_id = 1, new_address = "204 Hoàng Diệu", change_status = "PENDING", created_at = Now.AddDays(-1) });
        // Vendor 1 returned a second slot early but still trades on slot 1: that is not "stopped trading".
        f.Db.RentalApplications.Add(new RentalApplication { application_id = 4, registration_id = 1, slot_id = 2, application_method = "MANUAL_SELECTED", requested_term_days = 60, application_status = "APPROVED" });
        f.Db.RentalContracts.Add(new RentalContract
        {
            contract_id = 4, application_id = 4, slot_id = 2, vendor_id = Vendor1, start_date = new DateOnly(2026, 8, 1),
            end_date = new DateOnly(2026, 12, 1), contract_status = "CANCELLED", cancelled_at = Now.AddDays(-3), created_at = Now.AddDays(-80),
        });
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();
    }

    private static Violation Violation(long id, string type, long? contractId, long? slotId, long? vendorId, long recordedBy, DateTime at) => new()
    {
        violation_id = id, violation_type = type, contract_id = contractId, slot_id = slotId, vendor_id = vendorId,
        recorded_by = recordedBy, recorder_role = "WARD_AUTHORITY", source = "ON_SITE", recorded_at = at, bien_ban_so = $"{id:0000}/BB-VPHC",
    };

    private static PermitScanLog Scan(long id, long? permitId, string result, long? scannedBy, DateTime at, string context) => new()
    {
        scan_id = id, permit_id = permitId, qr_payload = $"QR-{id}", scanned_by = scannedBy, scanner_role = scannedBy is null ? null : "WARD_AUTHORITY", scan_context = context,
        scan_result = result, scanned_at = at,
    };

    [Fact]
    public async Task Refreshing_finds_what_the_ward_should_pass_to_the_tax_office_in_its_own_records()
    {
        using var f = await Fixture.Create();
        await AddReferralScenario(f);

        var result = await f.Repository.RefreshReferralsAsync(10, Now, 90, default);
        var referrals = await f.Repository.ListReferralsAsync(10, null, default);

        Assert.Equal(
            [
                "ADDRESS_CHANGED/ADDRESS_CHANGE/1",
                "BUSINESS_ENDED/CONTRACT/2",
                "INVALID_PERMIT_ACTIVITY/PERMIT_SCAN/2",
                "INVALID_PERMIT_ACTIVITY/VIOLATION/2",
                "NEW_HOUSEHOLD/VENDOR/2",
                "UNREGISTERED_ACTIVITY/PERMIT_SCAN/1",
                "UNREGISTERED_ACTIVITY/VIOLATION/1",
            ],
            referrals.Select(r => $"{r.ReasonCode}/{r.SourceType}/{r.SourceId}").Order());
        Assert.Equal(7, result.Created);
        Assert.Equal(0, result.AutoClosed);
        Assert.All(referrals, referral => Assert.Equal("PENDING", referral.Status));

        Assert.Equal(2, referrals.Count(r => r.ReasonCode == "UNREGISTERED_ACTIVITY")); // violation 1 + the forged-QR scan
        Assert.Equal(2, referrals.Count(r => r.ReasonCode == "INVALID_PERMIT_ACTIVITY")); // violation 2 + the revoked-permit scan
        Assert.Single(referrals, r => r.ReasonCode == "ADDRESS_CHANGED");
        Assert.Single(referrals, r => r.ReasonCode == "BUSINESS_ENDED");
        Assert.Single(referrals, r => r.ReasonCode == "NEW_HOUSEHOLD");

        var unlicensed = referrals.Single(r => r.SourceType == "VIOLATION" && r.SourceId == 1);
        Assert.Contains("NVL-01", unlicensed.Summary);
        Assert.Contains("Chưa xác định chủ hộ", unlicensed.Summary);
        Assert.Null(unlicensed.VendorId);
        Assert.Equal(Now.AddDays(-3), unlicensed.DetectedAt);

        var ended = referrals.Single(r => r.ReasonCode == "BUSINESS_ENDED");
        Assert.Equal(Vendor2, ended.VendorId); // vendor 2's only contract expired; vendor 1's cancelled one does not count
        Assert.Equal("Xe nước ép", ended.VendorName);
        Assert.Contains("NVL-02", ended.Summary);
        var address = referrals.Single(r => r.ReasonCode == "ADDRESS_CHANGED");
        Assert.Contains("200 Hoàng Diệu", address.Summary);
        var newcomer = referrals.Single(r => r.ReasonCode == "NEW_HOUSEHOLD");
        Assert.Equal(Vendor2, newcomer.VendorId);
        Assert.Equal("VENDOR", newcomer.SourceType);
    }

    [Fact]
    public async Task What_is_outside_the_window_or_in_order_is_not_a_referral()
    {
        using var f = await Fixture.Create();
        await AddReferralScenario(f);

        await f.Repository.RefreshReferralsAsync(10, Now, 90, default);
        var referrals = await f.Repository.ListReferralsAsync(10, null, default);

        Assert.DoesNotContain(referrals, r => r.SourceType == "VIOLATION" && r.SourceId == 3); // OUTSIDE_SLOT: not about registration
        Assert.DoesNotContain(referrals, r => r.SourceType == "VIOLATION" && r.SourceId == 4); // 200 days ago
        Assert.DoesNotContain(referrals, r => r.SourceType == "PERMIT_SCAN" && r.SourceId == 3); // a valid permit
        Assert.DoesNotContain(referrals, r => r.SourceType == "PERMIT_SCAN" && r.SourceId == 4); // a customer's check, not an inspection
        Assert.DoesNotContain(referrals, r => r.SourceType == "CONTRACT" && r.SourceId == 4); // vendor 1 still trades
        Assert.DoesNotContain(referrals, r => r.SourceType == "VENDOR" && r.SourceId == Vendor1); // already has a tax code
    }

    [Fact]
    public async Task Refreshing_again_adds_nothing_and_a_ward_only_gets_its_own()
    {
        using var f = await Fixture.Create();
        await AddReferralScenario(f);

        await f.Repository.RefreshReferralsAsync(10, Now, 90, default);
        var again = await f.Repository.RefreshReferralsAsync(10, Now.AddHours(1), 90, default);
        Assert.Equal(0, again.Created);
        Assert.Empty(await f.Repository.ListReferralsAsync(11, null, default));

        var all = await f.Repository.RefreshReferralsAsync(null, Now.AddHours(2), 90, default);
        Assert.Equal(2, all.Created); // ward 11: the unlicensed violation recorded by its officer, and vendor 3 without a tax code
        var ward11 = await f.Repository.ListReferralsAsync(11, null, default);
        Assert.Equal(["NEW_HOUSEHOLD", "UNREGISTERED_ACTIVITY"], ward11.Select(r => r.ReasonCode).Order());
        Assert.Equal(7, (await f.Repository.ListReferralsAsync(10, null, default)).Count);
    }

    [Fact]
    public async Task A_household_that_gives_its_tax_code_closes_its_own_referral()
    {
        using var f = await Fixture.Create();
        await AddReferralScenario(f);
        await f.Repository.RefreshReferralsAsync(10, Now, 90, default);

        await f.Repository.SaveProfileAsync(Vendor2, 6, "048090005678", "FOOD_SERVICE", false, false, Now.AddDays(1), default);
        var result = await f.Repository.RefreshReferralsAsync(10, Now.AddDays(1), 90, default);

        Assert.Equal(1, result.AutoClosed);
        var closed = (await f.Repository.ListReferralsAsync(10, "DISMISSED", default)).Single();
        Assert.Equal("NEW_HOUSEHOLD", closed.ReasonCode);
        Assert.Null(closed.HandledBy);
        Assert.Equal(Now.AddDays(1), closed.HandledAt);
        Assert.Contains("mã số thuế", closed.HandlingNote);
        Assert.DoesNotContain(await f.Repository.ListReferralsAsync(10, "PENDING", default), r => r.ReasonCode == "NEW_HOUSEHOLD");
    }

    [Fact]
    public async Task An_officer_handles_a_pending_referral_of_their_ward_once()
    {
        using var f = await Fixture.Create();
        await AddReferralScenario(f);
        await f.Repository.RefreshReferralsAsync(10, Now, 90, default);
        var target = (await f.Repository.ListReferralsAsync(10, "PENDING", default)).First(r => r.ReasonCode == "ADDRESS_CHANGED");

        Assert.Null(await f.Repository.HandleReferralAsync(11, target.ReferralId, OfficerWard11, "REPORTED", null, null, Now, default)); // not their ward
        var reported = await f.Repository.HandleReferralAsync(10, target.ReferralId, OfficerWard10, "REPORTED", "CV-12/PHƯỜNG", "Gửi thuế cơ sở", Now.AddHours(1), default);

        Assert.Equal("REPORTED", reported!.Status);
        Assert.Equal(OfficerWard10, reported.HandledBy);
        Assert.Equal("Cán bộ 2", reported.HandledByName);
        Assert.Equal(Now.AddHours(1), reported.HandledAt);
        Assert.Equal("CV-12/PHƯỜNG", reported.AuthorityReference);
        Assert.Null(await f.Repository.HandleReferralAsync(10, target.ReferralId, OfficerWard10, "DISMISSED", null, "đã xong", Now, default)); // no longer pending
        Assert.Equal("TAX_REFERRAL_REPORTED", (await f.Db.AuditLogs.SingleAsync(row => row.entity_type == "TaxReferral")).action);
    }

    [Fact]
    public async Task Referrals_list_pending_first_then_the_newest_events()
    {
        using var f = await Fixture.Create();
        await AddReferralScenario(f);
        await f.Repository.RefreshReferralsAsync(10, Now, 90, default);
        var oldest = (await f.Repository.ListReferralsAsync(10, null, default)).OrderBy(r => r.DetectedAt).First();
        await f.Repository.HandleReferralAsync(10, oldest.ReferralId, OfficerWard10, "DISMISSED", null, "không cần", Now, default);

        var list = await f.Repository.ListReferralsAsync(10, null, default);

        Assert.Equal(oldest.ReferralId, list[^1].ReferralId); // handled ones sink to the bottom
        var pending = list.Take(6).ToList();
        Assert.All(pending, r => Assert.Equal("PENDING", r.Status));
        Assert.Equal(pending.OrderByDescending(r => r.DetectedAt).Select(r => r.ReferralId), pending.Select(r => r.ReferralId));
    }

    // ------------------------------------------------------------------------- announcements

    [Fact]
    public async Task An_announcement_reaches_the_households_of_the_chosen_group()
    {
        using var f = await Fixture.Create();
        await f.Repository.SaveDeclarationAsync(Vendor1, 5, Filing(), Now, default);

        var everyone = await f.Repository.ListRecipientUserIdsAsync(10, "ALL", 2026, default);
        var notFiled = await f.Repository.ListRecipientUserIdsAsync(10, "NOT_FILED", 2026, default);
        var noCode = await f.Repository.ListRecipientUserIdsAsync(10, "NO_TAX_CODE", 2026, default);

        Assert.Equal([5L, 6L], everyone.Order());
        Assert.Equal([6L], notFiled); // user 5 already filed
        Assert.Equal([6L], noCode); // user 5 has a tax code
        Assert.Empty(await f.Repository.ListRecipientUserIdsAsync(99, "ALL", 2026, default));
    }

    [Fact]
    public async Task Sending_an_announcement_stores_it_and_notifies_every_recipient_in_one_go()
    {
        using var f = await Fixture.Create();

        var announcement = await f.Repository.CreateAnnouncementAsync(
            10, OfficerWard10, "ALL", 2026, "Nhắc thông báo doanh thu", "Hạn 31/01/2027.", [5, 6], Now, default);
        var listed = await f.Repository.ListAnnouncementsAsync(10, default);

        Assert.Equal(2, announcement.RecipientCount);
        Assert.Equal("Cán bộ 2", announcement.CreatedByName);
        Assert.Single(listed);
        Assert.Empty(await f.Repository.ListAnnouncementsAsync(11, default));
        var notices = await f.Db.Notifications.AsNoTracking().Where(n => n.notification_type == "TAX").ToListAsync();
        Assert.Equal([5L, 6L], notices.Select(n => n.user_id).Order());
        Assert.All(notices, n =>
        {
            Assert.Equal("TAX_ANNOUNCEMENT", n.related_entity_type);
            Assert.Equal(announcement.AnnouncementId, n.related_entity_id);
            Assert.False(n.is_read);
        });
        Assert.Single(f.Db.AuditLogs.Where(row => row.action == "TAX_ANNOUNCEMENT_SENT"));
    }

    // ------------------------------------------------------------------------------- sweeps

    [Fact]
    public async Task A_notification_keyed_on_a_record_is_sent_only_once()
    {
        using var f = await Fixture.Create();
        var repository = f.Repository;

        Assert.True(await repository.NotifyOnceAsync(5, "TAX", "Gần ngưỡng", "…", "TAX_THRESHOLD", 20261, Now, default));
        Assert.False(await repository.NotifyOnceAsync(5, "TAX", "Gần ngưỡng", "…", "TAX_THRESHOLD", 20261, Now.AddDays(1), default));
        Assert.True(await repository.NotifyOnceAsync(5, "TAX", "Vượt ngưỡng", "…", "TAX_THRESHOLD", 20262, Now, default)); // another record
        Assert.True(await repository.NotifyOnceAsync(6, "TAX", "Gần ngưỡng", "…", "TAX_THRESHOLD", 20261, Now, default)); // another user

        Assert.Equal(3, await f.Db.Notifications.CountAsync());
    }

    [Fact]
    public async Task Taxable_households_are_those_with_an_approved_registration_once_each()
    {
        using var f = await Fixture.Create();

        var vendors = await f.Repository.ListTaxableVendorsAsync(default);

        Assert.Equal([(1L, 5L), (2L, 6L), (3L, 7L)], vendors.OrderBy(v => v.VendorId));
    }

    // --------------------------------------------------------------------------------- setup

    private static UserAccount User(long id, string role, int? ward, string name) => new()
    {
        user_id = id, phone_number = $"09050001{id:00}", password_hash = "test-only", full_name = name, role_code = role,
        ward_unit_id = ward, account_status = "ACTIVE", created_at = Now.AddDays(-300),
    };

    private sealed class Fixture : IDisposable
    {
        private SqliteConnection Connection { get; } = new("Data Source=:memory:");
        public TestContext Db { get; private set; } = null!;

        public TaxRepository Repository => new(Db);

        public static async Task<Fixture> Create()
        {
            var f = new Fixture();
            await f.Connection.OpenAsync();
            f.Db = new TestContext(new DbContextOptionsBuilder<StreetBizDbContext>().UseSqlite(f.Connection).Options);
            await f.Db.Database.EnsureCreatedAsync();
            var db = f.Db;

            db.Roles.AddRange(
                new Role { role_code = "PLATFORM_ADMIN", role_name = "Admin" },
                new Role { role_code = "WARD_AUTHORITY", role_name = "Ward" },
                new Role { role_code = "VENDOR", role_name = "Vendor" },
                new Role { role_code = "CUSTOMER", role_name = "Customer" });
            db.AdministrativeUnits.AddRange(
                new AdministrativeUnit { unit_id = 10, unit_type = "WARD", unit_name = "Hải Châu 1" },
                new AdministrativeUnit { unit_id = 11, unit_type = "WARD", unit_name = "Thanh Khê Đông" });
            db.UserAccounts.AddRange(
                User(AdminUser, "PLATFORM_ADMIN", null, "Admin"),
                User(OfficerWard10, "WARD_AUTHORITY", 10, "Cán bộ 2"),
                User(OfficerWard11, "WARD_AUTHORITY", 11, "Cán bộ 3"),
                User(5, "VENDOR", 10, "Phạm Thị Lan"),
                User(6, "VENDOR", 10, "Hoàng Văn Tám"),
                User(7, "VENDOR", 11, "Đỗ Thị Mai"),
                User(9, "CUSTOMER", 10, "Khách"));
            db.Vendors.AddRange(
                new Vendor { vendor_id = Vendor1, user_id = 5 },
                new Vendor { vendor_id = Vendor2, user_id = 6 },
                new Vendor { vendor_id = Vendor3, user_id = 7 });
            db.BusinessRegistrations.AddRange(
                Registration(1, Vendor1, 10, "FIXED_STOREFRONT", "Bánh mì Cô Lan", Now.AddDays(-20), "123 Nguyễn Văn Linh", "Ăn uống"),
                Registration(4, Vendor1, 10, "FIXED_STOREFRONT", "Bún chả Hải Châu", Now.AddDays(-200), null, null),
                Registration(2, Vendor2, 10, "ITINERANT", "Xe nước ép", Now.AddDays(-10), null, null),
                Registration(3, Vendor3, 11, "FIXED_STOREFRONT", "Quán chè", Now.AddDays(-10), "88 Điện Biên Phủ", null));
            db.PricingZones.AddRange(
                new PricingZone { zone_id = 1, ward_unit_id = 10, zone_name = "NVL", created_by = AdminUser },
                new PricingZone { zone_id = 2, ward_unit_id = 11, zone_name = "ĐBP", created_by = AdminUser });
            db.SidewalkSlots.AddRange(
                new SidewalkSlot { slot_id = 1, zone_id = 1, slot_code = "NVL-01", source = "WARD_DEFINED", slot_status = "ACTIVE" },
                new SidewalkSlot { slot_id = 2, zone_id = 1, slot_code = "NVL-02", source = "WARD_DEFINED", slot_status = "ACTIVE" },
                new SidewalkSlot { slot_id = 3, zone_id = 2, slot_code = "ĐBP-01", source = "WARD_DEFINED", slot_status = "ACTIVE" });
            db.RentalApplications.AddRange(
                Application(1, 1, 1), Application(2, 2, 2), Application(3, 3, 3));
            db.RentalContracts.AddRange(
                Contract(1, 1, 1, Vendor1, new DateOnly(2026, 9, 1), new DateOnly(2026, 12, 29), "ACTIVE"),
                Contract(2, 2, 2, Vendor2, new DateOnly(2026, 7, 1), new DateOnly(2026, 10, 13), "EXPIRED"), // ended five days ago
                Contract(3, 3, 3, Vendor3, new DateOnly(2026, 9, 1), new DateOnly(2026, 12, 29), "ACTIVE"));
            db.DigitalPermits.Add(new DigitalPermit
            {
                permit_id = 1, contract_id = 1, qr_payload = "SEED-QR-1", permit_status = "ACTIVE", issued_at = Now.AddDays(-30),
            });
            db.Storefronts.AddRange(
                new Storefront { storefront_id = 1, registration_id = 1, contract_id = 1, storefront_name = "Bánh mì Cô Lan", availability_status = "OPEN", created_at = Now.AddDays(-20) },
                new Storefront { storefront_id = 2, registration_id = 2, contract_id = 2, storefront_name = "Xe nước ép", availability_status = "OPEN", created_at = Now.AddDays(-10) });
            db.ViolationTypes.AddRange(
                new ViolationType { violation_type_code = "NO_PERMIT", description = "Kinh doanh trên vỉa hè không có giấy phép", is_active = false },
                new ViolationType { violation_type_code = "EXPIRED_OR_INVALID_PERMIT", description = "Giấy phép hết hiệu lực hoặc sai nội dung", is_active = true },
                new ViolationType { violation_type_code = "OUTSIDE_SLOT", description = "Bày bán vượt ra ngoài ô", is_active = true });
            db.VendorTaxProfiles.Add(new VendorTaxProfile
            {
                vendor_id = Vendor1, tax_code = "048090001234", industry_code = "FOOD_SERVICE", share_revenue_with_ward = true,
                consent_updated_at = Now.AddDays(-20), created_at = Now.AddDays(-20),
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            return f;
        }

        private static BusinessRegistration Registration(
            long id, long vendorId, int ward, string type, string name, DateTime approvedAt, string? address, string? line) => new()
        {
            registration_id = id, vendor_id = vendorId, ward_unit_id = ward, vendor_type = type, display_name = name,
            declared_address = address, business_line = line, registration_status = "APPROVED", reviewed_at = approvedAt,
            created_at = approvedAt.AddDays(-4),
        };

        private static RentalApplication Application(long id, long registrationId, long slotId) => new()
        {
            application_id = id, registration_id = registrationId, slot_id = slotId, application_method = "MANUAL_SELECTED",
            requested_term_days = 90, application_status = "APPROVED",
        };

        private static RentalContract Contract(long id, long applicationId, long slotId, long vendorId, DateOnly start, DateOnly end, string status) => new()
        {
            contract_id = id, application_id = applicationId, slot_id = slotId, vendor_id = vendorId, start_date = start, end_date = end,
            contract_status = status, created_at = Now.AddDays(-60),
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
