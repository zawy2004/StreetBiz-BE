using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Infrastructure.Persistence.Repositories;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;
using StreetBiz.Infrastructure.Tests.Support;
using static StreetBiz.Infrastructure.Tests.Support.SqliteTestDatabase;

namespace StreetBiz.Infrastructure.Tests;

/// <summary>BUY-03..07 public side: who is trading legally right now, their profile, comments, QR checks and reports.</summary>
public sealed class CommunityVendorRepositoryTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 3, 0, 0, DateTimeKind.Utc);

    private static async Task<SqliteTestDatabase> SeedAsync()
    {
        var db = await CreateAsync();
        // The two SQL Server views are not created by EnsureCreated; tables of the same shape stand in.
        await db.Db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE vw_PermitValidity (permit_id INTEGER, contract_id INTEGER, qr_payload TEXT, slot_id INTEGER,
              vendor_id INTEGER, start_date TEXT, end_date TEXT, permit_status TEXT, contract_status TEXT, effective_status TEXT);
            CREATE TABLE vw_VendorRatings (vendor_id INTEGER, verified_count INTEGER, verified_rating TEXT, community_count INTEGER, community_rating TEXT);
            """);

        // Vendor B trades on slot 2 under its own approved itinerant registration; vendor C's permit is suspended.
        db.Db.RentalApplications.AddRange(
            new RentalApplication { application_id = 2, registration_id = RegistrationB, slot_id = SlotFree, application_method = "OPEN_SLOT", requested_term_days = 90, application_status = "APPROVED" },
            new RentalApplication { application_id = 3, registration_id = RegistrationB, slot_id = SlotFree2, application_method = "OPEN_SLOT", requested_term_days = 90, application_status = "APPROVED" });
        db.Db.RentalContracts.AddRange(
            new RentalContract { contract_id = 2, application_id = 2, slot_id = SlotFree, vendor_id = VendorB, start_date = new DateOnly(2026, 9, 1), end_date = new DateOnly(2026, 12, 1), contract_status = "ACTIVE" },
            new RentalContract { contract_id = 3, application_id = 3, slot_id = SlotFree2, vendor_id = VendorB, start_date = new DateOnly(2026, 9, 1), end_date = new DateOnly(2026, 12, 1), contract_status = "ACTIVE" });
        db.Db.DigitalPermits.AddRange(
            new DigitalPermit { permit_id = 1, contract_id = ContractA, qr_payload = "QR-A", permit_status = "ACTIVE", issued_at = T0 },
            new DigitalPermit { permit_id = 2, contract_id = 2, qr_payload = "QR-B", permit_status = "ACTIVE", issued_at = T0 },
            new DigitalPermit { permit_id = 3, contract_id = 3, qr_payload = "QR-B2", permit_status = "SUSPENDED", issued_at = T0 });
        db.Db.UserAccounts.Add(new UserAccount { user_id = 2, phone_number = "0900000002", password_hash = "x", full_name = "Cán bộ 2", role_code = "WARD_AUTHORITY", ward_unit_id = Ward, account_status = "ACTIVE" });
        await db.Db.SaveChangesAsync();
        await db.Db.Database.ExecuteSqlRawAsync("""
            INSERT INTO vw_PermitValidity VALUES
              (1, 1, 'QR-A', 1, 10, '2026-09-01', '2026-12-01', 'ACTIVE', 'ACTIVE', 'VALID'),
              (2, 2, 'QR-B', 2, 11, '2026-09-01', '2026-12-01', 'ACTIVE', 'ACTIVE', 'VALID'),
              (3, 3, 'QR-B2', 3, 11, '2026-09-01', '2026-12-01', 'SUSPENDED', 'ACTIVE', 'SUSPENDED');
            -- Like the real view: one row per vendor, zero counts and no average when nobody rated yet.
            INSERT INTO vw_VendorRatings VALUES (10, 3, '4.5', 7, '4.2'), (11, 0, NULL, 0, NULL), (12, 0, NULL, 0, NULL);
            """);
        // ward_unit_type is a computed 'WARD' on SQL Server; the SQLite copy drops computed columns.
        await db.Db.BusinessRegistrations.ExecuteUpdateAsync(s => s.SetProperty(r => r.ward_unit_type, "WARD"));
        db.Db.ChangeTracker.Clear();
        return db;
    }

    private static CommunityVendorRepository Repo(SqliteTestDatabase db) => new(db.Db, new TestClock(T0));

    [Fact]
    public async Task Search_lists_only_valid_permits_with_ratings_and_filters_by_the_map_box()
    {
        using var db = await SeedAsync();

        var all = await Repo(db).SearchActiveAsync(new CommunityVendorSearchArea(null, null, null, null), 10, default);

        Assert.Equal([(VendorA, SlotRented), (VendorB, SlotFree)], all.Select(r => (r.VendorId, r.SlotId)));
        var a = all[0];
        Assert.Equal(("Quán A", "S-01", "Nguyễn Văn Linh", 4.2m, 7, 4.5m, 3), (a.DisplayName, a.SlotCode, a.ZoneName, a.CommunityRating, a.CommunityCount, a.VerifiedRating, a.VerifiedCount));
        Assert.Equal(((decimal?)null, 0), (all[1].CommunityRating, all[1].CommunityCount)); // nobody rated yet

        var box = await Repo(db).SearchActiveAsync(new CommunityVendorSearchArea(16.0605m, 16.0611m, 108m, 109m), 10, default);
        Assert.Equal([VendorA], box.Select(r => r.VendorId));
        Assert.Single(await Repo(db).SearchActiveAsync(new CommunityVendorSearchArea(null, null, null, null), 1, default));
    }

    [Fact]
    public async Task A_suspended_account_disappears_from_the_map_and_has_no_public_profile()
    {
        using var db = await SeedAsync();
        await db.Db.UserAccounts.Where(u => u.user_id == VendorA).ExecuteUpdateAsync(s => s.SetProperty(u => u.account_status, "SUSPENDED"));

        var all = await Repo(db).SearchActiveAsync(new CommunityVendorSearchArea(null, null, null, null), 10, default);

        Assert.DoesNotContain(all, r => r.VendorId == VendorA);
        Assert.Null(await Repo(db).GetPublicProfileAsync(VendorA, default));
    }

    [Fact]
    public async Task The_public_profile_carries_the_ward_name_and_the_valid_permit()
    {
        using var db = await SeedAsync();

        var profile = await Repo(db).GetPublicProfileAsync(VendorB, default);

        Assert.NotNull(profile);
        Assert.Equal((2L, "VALID", Ward, "Phường Một", "S-02"), (profile!.PermitId, profile.PermitStatus, profile.WardId, profile.WardName, profile.SlotCode));
        Assert.Null(await Repo(db).GetPublicProfileAsync(VendorC, default));
    }

    [Fact]
    public async Task A_qr_check_needs_the_matching_payload_and_reports_the_effective_status()
    {
        using var db = await SeedAsync();

        var valid = await Repo(db).GetPermitVerificationAsync(ContractA, "QR-A", default);
        var suspended = await Repo(db).GetPermitVerificationAsync(3, "QR-B2", default);

        Assert.Equal(("Quán A", "VALID", new DateOnly(2026, 12, 1)), (valid!.DisplayName, valid.EffectiveStatus, valid.EndDate));
        Assert.Equal("SUSPENDED", suspended!.EffectiveStatus);
        Assert.Null(await Repo(db).GetPermitVerificationAsync(ContractA, "QR-B", default));
    }

    [Fact]
    public async Task A_public_scan_is_logged_as_a_public_check_at_the_clock_time()
    {
        using var db = await SeedAsync();

        await Repo(db).AddPermitScanAsync(new PublicPermitScan(1, "QR-A", Customer, "VALID", 16.06m, 108.21m, null), default);

        using var read = db.NewContext();
        var log = await read.PermitScanLogs.SingleAsync();
        Assert.Equal((1L, PermitScanContexts.PublicCheck, "VALID", T0), (log.permit_id!.Value, log.scan_context, log.scan_result, log.scanned_at));
    }

    [Fact]
    public async Task One_comment_per_customer_is_updated_in_place_and_comments_list_newest_first()
    {
        using var db = await SeedAsync();
        db.Db.UserAccounts.Add(new UserAccount { user_id = 21, phone_number = "0900000021", password_hash = "x", role_code = "CUSTOMER", account_status = "ACTIVE" });
        await db.Db.SaveChangesAsync();

        var first = await Repo(db).UpsertCommentAsync(VendorA, Customer, new CustomerVendorComment(3, "Tạm được"), default);
        var edited = await Repo(db).UpsertCommentAsync(VendorA, Customer, new CustomerVendorComment(5, "Ngon hơn rồi"), default);
        using (var other = db.NewContext())
        {
            other.VendorComments.Add(new VendorComment { vendor_id = VendorA, customer_user_id = 21, rating = 4, comment_text = "Ổn", created_at = T0.AddDays(1) });
            await other.SaveChangesAsync();
        }

        Assert.Equal(first.CommentId, edited.CommentId);
        Assert.Equal(((short?)5, "Ngon hơn rồi", "User 20"), (edited.Rating, edited.CommentText, edited.AuthorName));
        var comments = await Repo(db).ListCommentsAsync(VendorA, 10, default);
        Assert.Equal([21L, Customer], comments.Select(c => c.CustomerUserId));
        Assert.Equal("Khách hàng", comments[0].AuthorName); // no name on the account
        Assert.Single(await Repo(db).ListCommentsAsync(VendorA, 1, default));
    }

    [Theory]
    [InlineData(SlotRented, 1L, true)]
    [InlineData(null, null, true)]
    [InlineData(SlotFree, null, false)]  // vendor B's slot
    [InlineData(null, 2L, false)]        // vendor B's permit
    public async Task A_report_may_only_point_at_the_vendors_own_slot_and_permit(long? slotId, long? permitId, bool expected)
    {
        using var db = await SeedAsync();

        Assert.Equal(expected, await Repo(db).ReportReferencesBelongToVendorAsync(VendorA, slotId, permitId, default));
    }

    [Fact]
    public async Task A_report_reaches_the_ward_officers_and_the_vendor_and_a_second_pending_one_is_refused()
    {
        using var db = await SeedAsync();
        var notices = new NewVendorReportNotices(new("Báo cáo mới", "Có người báo Quán A"), new("Có báo cáo về bạn", "Phường sẽ xem xét"));

        var id = await Repo(db).AddReportAsync(VendorA, Customer, new CustomerVendorReport("Lấn chiếm vỉa hè", null, SlotRented, 1), notices, default);

        using (var read = db.NewContext())
        {
            var report = await read.VendorReports.SingleAsync();
            Assert.Equal((id, VendorReportStatuses.Pending, "Lấn chiếm vỉa hè", T0), (report.report_id, report.report_status, report.report_reason, report.created_at));
            var notified = await read.Notifications.Where(n => n.related_entity_id == id).Select(n => n.user_id).OrderBy(u => u).ToListAsync();
            Assert.Equal([Officer, 2L, VendorA], notified);
            Assert.True(await Repo(db).HasPendingReportAsync(Customer, VendorA, default));
        }
    }

    [Fact]
    public async Task The_report_target_needs_a_vendor_with_a_registration()
    {
        using var db = await SeedAsync();

        var target = await Repo(db).GetReportTargetAsync(VendorA, 1, default);

        Assert.Equal((VendorA, "Quán A", "S-01"), (target!.VendorId, target.DisplayName, target.SlotCode));
        Assert.Null(await Repo(db).GetReportTargetAsync(999, null, default));
        Assert.Null(await Repo(db).GetReportTargetAsync(VendorC, null, default)); // no registration, no ward to send it to
    }
}
