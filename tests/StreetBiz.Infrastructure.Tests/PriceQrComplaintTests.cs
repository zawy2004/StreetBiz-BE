using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Options;
using Moq;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.AiMarketplace;
using StreetBiz.Application.Features.Commerce;
using StreetBiz.Application.Features.CommunityVendors;
using StreetBiz.Application.Features.VendorReports;
using StreetBiz.Application.Features.WardSlots;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Persistence.Repositories;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;
using StreetBiz.Infrastructure.Security;
using StreetBiz.Infrastructure.Services;

namespace StreetBiz.Infrastructure.Tests;

/// <summary>
/// BUY-06 / BUY-07 / WARD-19 against a real (SQLite) schema built from the EF model: the append-only
/// price history written with every board change, the live public board, complaints judged against
/// the price in force at purchase, the one-pending-report rule, notices that never name the reporter
/// to the vendor, and the officer closing a report. The SQL Server trigger and CHECK constraints are
/// verified separately with sqlcmd (SQLite runs neither).
/// </summary>
public sealed class PriceQrComplaintTests
{
    private static readonly DateTime T0 = new(2026, 10, 20, 2, 0, 0, DateTimeKind.Utc); // Tuesday 09:00 in Đà Nẵng

    #region Price history and board (BUY-06)

    [Fact]
    public async Task Every_change_to_what_the_board_says_appends_a_full_snapshot_and_nothing_else_does()
    {
        using var f = await Fixture.Create();
        var dish = await f.AddDish("Bún chả", 45_000, "suất", "Grilled pork noodles");

        await f.Menu().SaveMenu(1, dish, Input("Bún chả", 45_000, "suất", "Grilled pork noodles", description: "Chả nướng than"), default);
        f.Clock.Advance(TimeSpan.FromMinutes(5));
        await f.Menu().SaveMenu(1, dish, Input("Bún chả", 50_000, "suất", "Grilled pork noodles"), default);
        await f.Menu().SaveMenu(1, dish, Input("Bún chả", 50_000, "tô", "Grilled pork noodles"), default);
        await f.Menu().SaveMenu(1, dish, Input("Bún chả", 50_000, "tô", "Grilled pork noodles", status: "SOLD_OUT"), default);
        await f.Menu().ArchiveMenu(1, dish, default);

        var rows = await f.History(dish);
        Assert.Equal(["CREATED", "UPDATED", "UPDATED", "ARCHIVED"], rows.Select(r => r.change_kind));
        Assert.Equal([45_000m, 50_000m, 50_000m, 50_000m], rows.Select(r => r.unit_price));
        Assert.Equal(["suất", "suất", "tô", "tô"], rows.Select(r => r.price_unit));
        Assert.Equal([true, true, true, false], rows.Select(r => r.is_listed));
        // BR-46: every row says who changed it and when; the vendor's own account here.
        Assert.All(rows, r => Assert.Equal(1, r.changed_by));
        Assert.All(rows, r => Assert.Equal("Grilled pork noodles", r.item_name_en));
        Assert.Equal(T0.AddMinutes(5), rows[1].changed_at);
        Assert.All(rows, r => Assert.Equal(1, r.storefront_id));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("một đơn vị dài hơn ba mươi ký tự")]
    public async Task A_dish_cannot_be_saved_without_a_unit_of_sale(string? unit)
    {
        using var f = await Fixture.Create();

        await Assert.ThrowsAsync<DomainRuleException>(() => f.Menu().SaveMenu(1, null,
            new SellerMenuInput(1, "Bún chả", null, 45_000, "AVAILABLE", Fixture.Photo, unit), default));
        Assert.Empty(await f.Db.MenuItems.AsNoTracking().ToListAsync());
        Assert.Empty(await f.Db.MenuItemPriceHistories.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(50_000_001)]
    [InlineData(10_000.5)]
    public async Task A_price_stays_a_whole_vnd_amount_between_one_and_fifty_million(double price)
    {
        using var f = await Fixture.Create();

        await Assert.ThrowsAsync<DomainRuleException>(() => f.Menu().SaveMenu(1, null,
            Input("Bún chả", (decimal)price, "suất"), default));
        Assert.Empty(await f.Db.MenuItemPriceHistories.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task The_vendor_sees_the_board_its_version_the_history_with_previous_prices_and_the_print_links()
    {
        using var f = await Fixture.Create();
        var dish = await f.AddDish("Bún chả", 45_000, "suất");
        f.Clock.Advance(TimeSpan.FromHours(1));
        await f.Menu().SaveMenu(1, dish, Input("Bún chả", 50_000, "suất"), default);

        var board = await f.Menu("http://192.168.1.5:5173/").PriceBoard(1, default);

        Assert.Equal(2, board.Version);
        Assert.Equal("A-01", board.SlotCode);
        Assert.Equal(T0.AddHours(1), board.UpdatedAt);
        var item = Assert.Single(board.Items);
        Assert.Equal((50_000m, "suất"), (item.UnitPrice, item.PriceUnit));
        Assert.Equal(["UPDATED", "CREATED"], board.History.Select(h => h.ChangeKind));
        Assert.Equal(45_000m, board.History[0].PreviousUnitPrice);
        Assert.Null(board.History[1].PreviousUnitPrice);
        Assert.Equal("Chủ hộ A", board.History[0].ChangedByName);
        // The QR on the paper board wraps the contract's signed token: no new code, no sequential id.
        Assert.Equal($"http://192.168.1.5:5173/q/{f.Token1}?v=2", board.QrUrl);
        Assert.Equal($"http://192.168.1.5:5173/q/{f.Token1}/print", board.PrintUrl);

        var unconfigured = await f.Menu().PriceBoard(1, default);
        Assert.Null(unconfigured.QrUrl);
        Assert.Null(unconfigured.PrintUrl);
        await Assert.ThrowsAsync<NotFoundException>(() => f.Menu().PriceBoard(2, default));
    }

    [Fact]
    public async Task A_moderator_hiding_and_restoring_a_dish_is_recorded_against_the_moderator()
    {
        using var f = await Fixture.Create();
        var dish = await f.AddDish("Bún chả", 45_000, "suất");
        var report = new ReportedContent
        {
            content_type = "MENU_ITEM", content_id = dish, reporter_user_id = 2, reason = "Ảnh phản cảm",
            status = "PENDING", created_at = T0,
        };
        f.Db.ReportedContents.Add(report);
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();
        var admin = new PlatformAdministrationRepository(f.Db, f.Clock);

        await admin.DecideReportedContentAsync(report.content_report_id, "PENDING", ContentModerationDecisions.Hide, 5, default);
        await admin.RestoreReportedContentAsync(report.content_report_id, "HIDDEN", 5, default);

        var rows = await f.History(dish);
        Assert.Equal(["CREATED", "ARCHIVED", "RESTORED"], rows.Select(r => r.change_kind));
        Assert.Equal([1L, 5L, 5L], rows.Select(r => r.changed_by));
        Assert.Equal([true, false, true], rows.Select(r => r.is_listed));
        var board = await f.Menu().PriceBoard(1, default);
        Assert.Equal("Quản trị viên", board.History[0].ChangedByName);
    }

    [Theory]
    [InlineData("OPEN")]
    [InlineData("PAUSED")]
    [InlineData("CLOSED")]
    public async Task The_public_board_shows_the_listed_dishes_whatever_the_storefronts_own_state(string storefrontStatus)
    {
        using var f = await Fixture.Create();
        await f.AddDish("Bún chả", 45_000, "suất");
        var soldOut = await f.AddDish("Nem cua", 30_000, "đĩa");
        await f.Menu().SaveMenu(1, soldOut, Input("Nem cua", 30_000, "đĩa", status: "SOLD_OUT"), default);
        var archived = await f.AddDish("Trà đá", 3_000, "ly");
        await f.Menu().ArchiveMenu(1, archived, default);
        (await f.Db.Storefronts.SingleAsync(s => s.storefront_id == 1)).availability_status = storefrontStatus;
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();

        var board = await f.Community().GetPriceBoardByContractAsync(1, default);

        Assert.NotNull(board);
        Assert.Equal(["Bún chả", "Nem cua"], board!.Items.Select(i => i.Name));
        Assert.Equal(4, board.Version);
        Assert.Null(await f.Community().GetPriceBoardByContractAsync(404, default));
    }

    [Fact]
    public async Task Every_slot_of_a_business_shows_its_one_board_and_gets_its_own_paper_board_with_its_own_qr()
    {
        using var f = await Fixture.Create();
        await f.AddDish("Bún chả", 45_000, "suất");
        // The same business rents a second slot: same storefront (one per registration), new permit.
        var token3 = await f.AddSlot(300, "A-02", contractId: 3, permitId: 3);
        // A slot whose permit was revoked, and the demo seed's placeholder that can never verify.
        await f.AddSlot(400, "A-03", contractId: 4, permitId: 4, permitStatus: PermitStatuses.Revoked);
        await f.AddSlot(500, "A-04", contractId: 5, permitId: 5, payload: "SEED-PERMIT-CONTRACT-5-DO-NOT-SCAN");

        var publicBoard = await f.Community().GetPriceBoardByContractAsync(3, default);
        Assert.Equal(1, publicBoard!.StorefrontId);
        Assert.Equal(["Bún chả"], publicBoard.Items.Select(i => i.Name));

        var board = await f.Menu("http://192.168.1.5:5173", tokens: true).PriceBoard(1, default);
        Assert.Equal(["A-01", "A-02"], board.Boards.Select(b => b.SlotCode));
        Assert.Equal($"http://192.168.1.5:5173/q/{token3}?v=1", board.Boards[1].QrUrl);
        Assert.Equal($"http://192.168.1.5:5173/q/{token3}/print", board.Boards[1].PrintUrl);
        // The top-level links stay those of the storefront's own slot.
        Assert.Equal(board.Boards[0].QrUrl, board.QrUrl);
        Assert.Equal($"http://192.168.1.5:5173/q/{f.Token1}?v=1", board.QrUrl);
    }

    #endregion

    #region Complaints (BUY-05 / BUY-07)

    [Fact]
    public async Task A_complaint_is_judged_against_the_price_posted_when_the_customer_paid()
    {
        using var f = await Fixture.Create();
        var dish = await f.AddDish("Bún chả", 45_000, "suất");
        var created = Assert.Single(await f.History(dish));
        f.Clock.Advance(TimeSpan.FromHours(3));
        await f.Menu().SaveMenu(1, dish, Input("Bún chả", 60_000, "suất"), default);
        f.Clock.Advance(TimeSpan.FromHours(2));
        await f.Menu().ArchiveMenu(1, dish, default);

        var atPurchase = await f.Community().FindPostedPriceAsync(10, dish, T0.AddHours(2), default);
        var afterRaise = await f.Community().FindPostedPriceAsync(10, dish, T0.AddHours(4), default);
        var afterArchive = await f.Community().FindPostedPriceAsync(10, dish, T0.AddHours(6), default);
        var beforeDish = await f.Community().FindPostedPriceAsync(10, dish, T0.AddHours(-1), default);

        Assert.Equal((created.price_history_id, 45_000m, true), (atPurchase!.PriceHistoryId!.Value, atPurchase.UnitPrice!.Value, atPurchase.IsListed));
        Assert.Equal(60_000m, afterRaise!.UnitPrice);
        Assert.False(afterArchive!.IsListed);
        Assert.Null(beforeDish!.PriceHistoryId);
        // Another vendor's dish is not this vendor's to be judged on.
        Assert.Null(await f.Community().FindPostedPriceAsync(11, dish, T0.AddHours(2), default));
    }

    [Fact]
    public async Task An_overcharge_is_stored_with_server_computed_evidence_and_notifies_the_ward_and_the_vendor_only()
    {
        using var f = await Fixture.Create();
        var dish = await f.AddDish("Bún chả", 45_000, "suất");
        f.Clock.Advance(TimeSpan.FromHours(3));
        // Repriced after the sale: the complaint must still use 45.000đ.
        await f.Menu().SaveMenu(1, dish, Input("Bún chả", 70_000, "suất"), default);
        f.Clock.Advance(TimeSpan.FromHours(1));

        var receipt = await f.ReportHandler(customerId: 2).Handle(new ReportSuspiciousVendorCommand(
            10, "Tôi là Nguyễn Khách Hàng, số 0905000201, bị tính 120.000đ.", null, null, null,
            VendorReportTypes.Overcharge, dish, 2, 120_000, new DateTimeOffset(T0.AddHours(2))), default);

        var report = await f.Db.VendorReports.AsNoTracking().SingleAsync();
        var posted = (await f.History(dish))[0];
        Assert.Equal(receipt.ReportId, report.report_id);
        Assert.Equal((VendorReportTypes.Overcharge, posted.price_history_id, 2, 90_000m, 120_000m),
            (report.report_type, report.price_history_id!.Value, report.quantity!.Value, report.posted_amount!.Value, report.charged_amount!.Value));
        Assert.Equal(T0.AddHours(2), report.purchased_at);
        // Tuesday 20/10/2026 + 7 working days = Thursday 29/10, to the end of that Vietnamese day.
        Assert.Equal(new DateTime(2026, 10, 29, 17, 0, 0, DateTimeKind.Utc), report.due_at);
        Assert.Equal(VendorReportStatuses.Pending, report.report_status);

        var notices = await f.Db.Notifications.AsNoTracking().ToListAsync();
        Assert.Equal([(3L, VendorReportNotificationTypes.New), (1L, VendorReportNotificationTypes.AgainstYou)],
            notices.OrderByDescending(n => n.user_id).Select(n => (n.user_id, n.notification_type)));
        var toVendor = notices.Single(n => n.user_id == 1);
        Assert.DoesNotContain("Nguyễn Khách Hàng", toVendor.title + toVendor.body);
        Assert.DoesNotContain("0905000201", toVendor.title + toVendor.body);
        Assert.DoesNotContain("Tôi là", toVendor.body);
        Assert.Contains("90.000đ", toVendor.body);
        Assert.All(notices, n => Assert.Equal(("VendorReport", report.report_id), (n.related_entity_type, n.related_entity_id!.Value)));
    }

    [Fact]
    public async Task One_pending_report_per_person_and_vendor_is_enforced_by_the_check_and_by_the_index()
    {
        using var f = await Fixture.Create();
        await f.ReportHandler(customerId: 2).Handle(new ReportSuspiciousVendorCommand(10, "Lấn vỉa hè", null, null, null), default);

        var second = await Assert.ThrowsAsync<ConflictException>(() => f.ReportHandler(customerId: 2)
            .Handle(new ReportSuspiciousVendorCommand(10, "Lại lấn vỉa hè", null, null, null), default));
        Assert.Equal(409, second.StatusCode);
        // Another customer, or the same customer about another vendor, may still report.
        await f.ReportHandler(customerId: 7).Handle(new ReportSuspiciousVendorCommand(10, "Lấn vỉa hè", null, null, null), default);
        await f.ReportHandler(customerId: 2).Handle(new ReportSuspiciousVendorCommand(11, "Lấn vỉa hè", null, null, null), default);

        // The filtered unique index is the backstop for two submissions racing past the check.
        f.Db.VendorReports.Add(Report(2, 10, VendorReportStatuses.Pending));
        await Assert.ThrowsAsync<DbUpdateException>(() => f.Db.SaveChangesAsync());
        f.Db.ChangeTracker.Clear();
        // A closed report does not count.
        f.Db.VendorReports.Add(Report(2, 10, VendorReportStatuses.Dismissed, resolution: VendorReportResolutions.Dismissed));
        await f.Db.SaveChangesAsync();
    }

    [Fact]
    public async Task My_reports_are_only_the_callers_newest_first_with_the_outcome_and_the_price_evidence()
    {
        using var f = await Fixture.Create();
        var dish = await f.AddDish("Bún chả", 45_000, "suất");
        f.Clock.Advance(TimeSpan.FromHours(1));
        await f.ReportHandler(customerId: 2).Handle(new ReportSuspiciousVendorCommand(
            10, null, null, null, null, VendorReportTypes.Overcharge, dish, 2, 120_000, new DateTimeOffset(T0.AddMinutes(30))), default);
        f.Clock.Advance(TimeSpan.FromHours(1));
        await f.ReportHandler(customerId: 7).Handle(new ReportSuspiciousVendorCommand(10, "Khác", null, null, null), default);

        var mine = await f.Community().ListMyReportsAsync(2, 1, 20, default);

        var report = Assert.Single(mine);
        Assert.Equal(("Quầy A", "A-01"), (report.VendorName, report.SlotCode));
        Assert.Equal("Bị tính cao hơn giá niêm yết: 2 suất Bún chả.", report.Reason);
        Assert.NotNull(report.PriceEvidence);
        Assert.Equal((45_000m, 1, 90_000m, 120_000m), (report.PriceEvidence!.PostedUnitPrice, report.PriceEvidence.PriceVersion,
            report.PriceEvidence.PostedAmount, report.PriceEvidence.ChargedAmount));
    }

    #endregion

    #region Closing a report (WARD-19)

    [Fact]
    public async Task Dismissing_records_who_when_why_audits_it_and_tells_both_sides_the_vendor_without_the_note()
    {
        using var f = await Fixture.Create();
        var reportId = await f.SeedReport(customerId: 2, VendorReportTypes.General);
        f.Clock.Advance(TimeSpan.FromHours(5));

        await f.Decisions().ResolveAsync(f.Officer1, reportId, new ResolveVendorReportRequest(
            VendorReportStatuses.Pending, VendorReportResolutions.Dismissed, "Đã kiểm tra, chị Hà đã nhận lại tiền."), default);

        var report = await f.Db.VendorReports.AsNoTracking().SingleAsync();
        Assert.Equal((VendorReportStatuses.Dismissed, VendorReportResolutions.Dismissed, 3L, T0.AddHours(5)),
            (report.report_status, report.resolution, report.reviewed_by!.Value, report.reviewed_at!.Value));
        var audit = await f.Db.AuditLogs.AsNoTracking().SingleAsync();
        Assert.Equal(("VENDOR_REPORT_RESOLVED", 3L, reportId), (audit.action, audit.actor_user_id, audit.entity_id));
        var notices = await f.Db.Notifications.AsNoTracking().Where(n => n.notification_type == VendorReportNotificationTypes.Resolved).ToListAsync();
        Assert.Contains("chị Hà", notices.Single(n => n.user_id == 2).body);
        var toVendor = notices.Single(n => n.user_id == 1);
        Assert.DoesNotContain("chị Hà", toVendor.body);
        Assert.DoesNotContain("Nguyễn Khách Hàng", toVendor.body);
    }

    [Fact]
    public async Task Mediation_may_record_a_refund_up_to_what_was_charged_and_no_more()
    {
        using var f = await Fixture.Create();
        var overcharge = await f.SeedReport(customerId: 2, VendorReportTypes.Overcharge, charged: 120_000);
        var general = await f.SeedReport(customerId: 7, VendorReportTypes.General);

        var tooMuch = await Assert.ThrowsAsync<WardException>(() => f.Decisions().ResolveAsync(f.Officer1, overcharge,
            new ResolveVendorReportRequest(VendorReportStatuses.Pending, VendorReportResolutions.Mediated, "Hoàn tiền", 130_000), default));
        var noCharge = await Assert.ThrowsAsync<WardException>(() => f.Decisions().ResolveAsync(f.Officer1, general,
            new ResolveVendorReportRequest(VendorReportStatuses.Pending, VendorReportResolutions.Mediated, "Hoàn tiền", 10_000), default));
        Assert.Equal(("refund_exceeds_charge", 400), (tooMuch.Code, tooMuch.StatusCode));
        Assert.Equal("refund_without_charge", noCharge.Code);
        Assert.All(await f.Db.VendorReports.AsNoTracking().ToListAsync(), r => Assert.Equal(VendorReportStatuses.Pending, r.report_status));

        await f.Decisions().ResolveAsync(f.Officer1, overcharge, new ResolveVendorReportRequest(
            VendorReportStatuses.Pending, VendorReportResolutions.Mediated, "Người bán hoàn 30.000đ", 30_000), default);

        var closed = await f.Db.VendorReports.AsNoTracking().SingleAsync(r => r.report_id == overcharge);
        Assert.Equal((VendorReportStatuses.Reviewed, VendorReportResolutions.Mediated, 30_000m),
            (closed.report_status, closed.resolution, closed.refund_amount!.Value));
    }

    [Fact]
    public async Task Two_officers_closing_the_same_report_one_gets_a_conflict_and_nothing_changes_twice()
    {
        using var f = await Fixture.Create();
        f.Db.UserAccounts.Add(Account(9, "WARD_AUTHORITY", "Cán bộ Ba", ward: 1));
        await f.Db.SaveChangesAsync();
        var reportId = await f.SeedReport(customerId: 2, VendorReportTypes.General);

        await f.Decisions().ResolveAsync(f.Officer1, reportId, new ResolveVendorReportRequest(
            VendorReportStatuses.Pending, VendorReportResolutions.Dismissed, "Không đủ căn cứ"), default);
        // The second officer still had the PENDING report open.
        var late = await Assert.ThrowsAsync<WardException>(() => f.Decisions().ResolveAsync(new WardActor(9, 1, "Cán bộ Ba"), reportId,
            new ResolveVendorReportRequest(VendorReportStatuses.Pending, VendorReportResolutions.Mediated, "Đã hòa giải"), default));
        var stale = await Assert.ThrowsAsync<WardException>(() => f.Decisions().ResolveAsync(f.Officer1, reportId,
            new ResolveVendorReportRequest(VendorReportStatuses.Reviewed, VendorReportResolutions.Mediated, "Đã hòa giải"), default));

        Assert.Equal(409, late.StatusCode);
        Assert.Equal(409, stale.StatusCode);
        var report = await f.Db.VendorReports.AsNoTracking().SingleAsync();
        Assert.Equal((VendorReportResolutions.Dismissed, 3L), (report.resolution, report.reviewed_by!.Value));
        Assert.Equal(1, await f.Db.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task Reports_never_close_record_or_fine_on_their_own_however_many_there_are()
    {
        // BR-41/43: a pile of complaints is context for the officer, never a decision by itself.
        using var f = await Fixture.Create();
        foreach (var customer in new long[] { 2, 7, 8 })
        {
            await f.ReportHandler(customer).Handle(new ReportSuspiciousVendorCommand(
                10, null, null, null, null, VendorReportTypes.PriceNotPosted), default);
        }

        Assert.All(await f.Db.VendorReports.AsNoTracking().ToListAsync(), r =>
        {
            Assert.Equal(VendorReportStatuses.Pending, r.report_status);
            Assert.Null(r.resolution);
            Assert.Null(r.reviewed_by);
        });
        Assert.Empty(await f.Db.Violations.AsNoTracking().ToListAsync());
        Assert.Empty(await f.Db.Penalties.AsNoTracking().ToListAsync());
        Assert.Empty(await f.Db.AuditLogs.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task The_schema_allows_one_violation_per_source_report()
    {
        using var f = await Fixture.Create();
        var reportId = await f.SeedReport(customerId: 2, VendorReportTypes.General);
        f.Db.ViolationTypes.Add(new ViolationType { violation_type_code = "PRICE_NOT_POSTED", description = "Không niêm yết giá", is_active = true });
        await f.Db.SaveChangesAsync();
        Violation Record() => new()
        {
            vendor_id = 10, violation_type = "PRICE_NOT_POSTED", recorded_by = 3, recorder_role = "WARD_AUTHORITY",
            source = "CUSTOMER_REPORT", source_report_id = reportId, recorded_at = T0,
        };

        f.Db.Violations.Add(Record());
        await f.Db.SaveChangesAsync();
        f.Db.Violations.Add(Record());

        await Assert.ThrowsAsync<DbUpdateException>(() => f.Db.SaveChangesAsync());
    }

    [Fact]
    public async Task Another_wards_report_is_not_found_for_the_officer()
    {
        using var f = await Fixture.Create();
        var reportId = await f.SeedReport(customerId: 2, VendorReportTypes.General);

        await Assert.ThrowsAsync<NotFoundException>(() => f.Decisions().ResolveAsync(new WardActor(4, 2, "Cán bộ Hai"), reportId,
            new ResolveVendorReportRequest(VendorReportStatuses.Pending, VendorReportResolutions.Dismissed, "Lý do"), default));
        Assert.Equal(VendorReportStatuses.Pending, (await f.Db.VendorReports.AsNoTracking().SingleAsync()).report_status);
    }

    #endregion

    #region Ward inbox and dashboard

    [Fact]
    public async Task The_inbox_puts_overdue_first_then_the_nearest_due_then_closed_and_filters_by_type_and_overdue()
    {
        using var f = await Fixture.Create();
        var now = f.Clock.GetUtcNow().UtcDateTime;
        var closed = await f.SeedReport(customerId: 7, VendorReportTypes.General, dueAt: now.AddDays(-9), created: now.AddDays(-10),
            status: VendorReportStatuses.Dismissed);
        var dueLater = await f.SeedReport(customerId: 2, VendorReportTypes.Overcharge, charged: 120_000, dueAt: now.AddDays(5));
        var overdue = await f.SeedReport(customerId: 7, VendorReportTypes.PriceNotPosted, dueAt: now.AddHours(-1), vendorId: 11);
        var legacy = await f.SeedReport(customerId: 8, VendorReportTypes.General, dueAt: null);
        var inbox = f.Inbox();

        var all = await inbox.ListVendorReportsForWardAsync(1, Filter(now), 1, 20, default);
        var overdueOnly = await inbox.ListVendorReportsForWardAsync(1, Filter(now, overdue: true), 1, 20, default);
        var notOverdue = await inbox.ListVendorReportsForWardAsync(1, Filter(now, overdue: false), 1, 20, default);
        var overcharges = await inbox.ListVendorReportsForWardAsync(1, Filter(now, type: VendorReportTypes.Overcharge), 1, 20, default);

        // Report "overdue" is about vendor 11 of ward 2 but names ward 1's slot, so ward 1 sees it too.
        Assert.Equal([overdue, dueLater, legacy, closed], all.Select(r => r.ReportId));
        Assert.Equal([overdue], overdueOnly.Select(r => r.ReportId));
        Assert.Equal([dueLater, legacy, closed], notOverdue.Select(r => r.ReportId));
        Assert.Equal([dueLater], overcharges.Select(r => r.ReportId));
    }

    [Fact]
    public async Task A_vendor_with_two_businesses_is_named_by_the_one_whose_board_priced_the_complaint()
    {
        using var f = await Fixture.Create();
        var dish = await f.AddDish("Bún chả", 45_000, "suất");
        // The same vendor later registered a second business, which is now its newest registration.
        f.Db.BusinessRegistrations.Add(new BusinessRegistration { registration_id = 3, vendor_id = 10, ward_unit_id = 1, vendor_type = "ITINERANT", display_name = "Xôi Cô A", registration_status = "APPROVED" });
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();
        f.Clock.Advance(TimeSpan.FromHours(1));
        await f.ReportHandler(customerId: 2).Handle(new ReportSuspiciousVendorCommand(
            10, null, null, null, null, VendorReportTypes.Overcharge, dish, 2, 120_000, new DateTimeOffset(T0.AddMinutes(30))), default);
        var reportId = (await f.Db.VendorReports.AsNoTracking().SingleAsync()).report_id;

        var row = await f.Inbox().GetVendorReportAsync(reportId, default);
        var listed = Assert.Single(await f.Inbox().ListVendorReportsForWardAsync(1, Filter(f.Clock.GetUtcNow().UtcDateTime), 1, 20, default));

        Assert.Equal("Quầy A", row!.VendorName);
        Assert.Equal("Quầy A", listed.VendorName);
    }

    [Fact]
    public async Task The_detail_carries_the_price_evidence_the_vendors_90_day_count_the_outcome_and_the_record()
    {
        using var f = await Fixture.Create();
        var dish = await f.AddDish("Bún chả", 45_000, "suất");
        f.Clock.Advance(TimeSpan.FromHours(1));
        await f.ReportHandler(customerId: 2).Handle(new ReportSuspiciousVendorCommand(
            10, null, null, null, null, VendorReportTypes.Overcharge, dish, 2, 120_000, new DateTimeOffset(T0.AddMinutes(30))), default);
        var reportId = (await f.Db.VendorReports.AsNoTracking().SingleAsync()).report_id;
        await f.SeedReport(customerId: 7, VendorReportTypes.General, status: VendorReportStatuses.Dismissed,
            created: f.Clock.GetUtcNow().UtcDateTime.AddDays(-30));
        await f.SeedReport(customerId: 8, VendorReportTypes.General, status: VendorReportStatuses.Dismissed,
            created: f.Clock.GetUtcNow().UtcDateTime.AddDays(-120));
        await f.Decisions().ResolveAsync(f.Officer1, reportId, new ResolveVendorReportRequest(
            VendorReportStatuses.Pending, VendorReportResolutions.Mediated, "Đã hoàn tiền", 30_000), default);

        var row = await f.Inbox().GetVendorReportAsync(reportId, default);

        Assert.NotNull(row!.PriceEvidence);
        Assert.Equal((45_000m, 1, 2, 90_000m, 120_000m), (row.PriceEvidence!.PostedUnitPrice, row.PriceEvidence.PriceVersion,
            row.PriceEvidence.Quantity, row.PriceEvidence.PostedAmount, row.PriceEvidence.ChargedAmount));
        Assert.Equal(2, row.ReportsAgainstVendor90Days);
        Assert.Equal((VendorReportResolutions.Mediated, 30_000m, "Cán bộ Một"),
            (row.Outcome!.Resolution, row.Outcome.RefundAmount!.Value, row.Outcome.ReviewedByName));
        Assert.Null(row.ViolationId);
    }

    [Fact]
    public async Task The_dashboard_counts_open_and_overdue_reports_and_the_average_time_to_close()
    {
        using var f = await Fixture.Create();
        var now = f.Clock.GetUtcNow().UtcDateTime;
        await f.SeedReport(customerId: 2, VendorReportTypes.General, dueAt: now.AddHours(-1));
        await f.SeedReport(customerId: 7, VendorReportTypes.General, dueAt: now.AddDays(3));
        await f.SeedReport(customerId: 8, VendorReportTypes.General, status: VendorReportStatuses.Dismissed,
            created: now.AddHours(-30), reviewedAt: now.AddHours(-20));
        await f.SeedReport(customerId: 8, VendorReportTypes.General, status: VendorReportStatuses.Dismissed,
            created: now.AddHours(-14), reviewedAt: now.AddHours(-10), vendorId: 11);
        // Closed long ago: outside the 30-day average.
        await f.SeedReport(customerId: 2, VendorReportTypes.General, status: VendorReportStatuses.Dismissed,
            created: now.AddDays(-60), reviewedAt: now.AddDays(-40));

        var dashboard = await new WardReportRepository(f.Db, f.Clock).GetDashboardAsync(1, DateOnly.FromDateTime(now), default);

        Assert.Equal((2, 1), (dashboard.OpenReports, dashboard.OverdueReports));
        Assert.Equal(7.0, dashboard.AvgResolutionHours30d);
    }

    #endregion

    private static SellerMenuInput Input(
        string name, decimal price, string? unit, string? nameEn = null, string? description = null, string status = "AVAILABLE") =>
        new(1, name, description, price, status, null, unit, nameEn);

    private static VendorReportListFilter Filter(DateTime now, string? type = null, bool? overdue = null) =>
        new(null, type, overdue, now);

    private static VendorReport Report(long reporter, long vendorId, string status, string? resolution = null) => new()
    {
        reporter_user_id = reporter, vendor_id = vendorId, report_reason = "x", report_status = status,
        report_type = VendorReportTypes.General, resolution = resolution, created_at = T0,
        resolution_note = resolution is null ? null : "Lý do",
        reviewed_by = resolution is null ? null : 3, reviewer_role = resolution is null ? null : "WARD_AUTHORITY",
        reviewed_at = resolution is null ? null : T0,
    };

    private static UserAccount Account(long id, string role, string name, int? ward = null, string status = "ACTIVE", string? phone = null) => new()
    {
        user_id = id, phone_number = phone ?? $"09000000{id:00}", password_hash = "test-only", full_name = name,
        role_code = role, ward_unit_id = ward, account_status = status,
    };

    private sealed class Fixture : IDisposable
    {
        public const string Photo = "/api/uploads/menu-images/1/0123456789abcdef0123456789abcdef.jpg";
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        private readonly PermitTokenService tokens = new(Options.Create(new PermitSettings { SigningKey = "test-only-signing-key-0123456789" }));
        public TestContext Db { get; private set; } = null!;
        public TestClock Clock { get; } = new(T0);
        public string Token1 { get; private set; } = null!;
        public WardActor Officer1 { get; } = new(3, 1, "Cán bộ Một");

        public static async Task<Fixture> Create()
        {
            var f = new Fixture();
            await f.connection.OpenAsync();
            f.Db = new TestContext(new DbContextOptionsBuilder<StreetBizDbContext>().UseSqlite(f.connection).Options);
            await f.Db.Database.EnsureCreatedAsync();
            f.Token1 = f.tokens.Create(1, T0);

            f.Db.Roles.AddRange(
                new Role { role_code = "VENDOR", role_name = "Vendor" },
                new Role { role_code = "CUSTOMER", role_name = "Customer" },
                new Role { role_code = "WARD_AUTHORITY", role_name = "Ward" },
                new Role { role_code = "PLATFORM_ADMIN", role_name = "Admin" });
            f.Db.AdministrativeUnits.AddRange(
                new AdministrativeUnit { unit_id = 1, unit_type = "WARD", unit_name = "Phường Một" },
                new AdministrativeUnit { unit_id = 2, unit_type = "WARD", unit_name = "Phường Hai" });
            f.Db.UserAccounts.AddRange(
                Account(1, "VENDOR", "Chủ hộ A"),
                Account(2, "CUSTOMER", "Nguyễn Khách Hàng", phone: "0905000201"),
                Account(3, "WARD_AUTHORITY", "Cán bộ Một", ward: 1),
                Account(4, "WARD_AUTHORITY", "Cán bộ Hai", ward: 2),
                Account(5, "PLATFORM_ADMIN", "Quản trị"),
                Account(6, "VENDOR", "Chủ hộ B"),
                Account(7, "CUSTOMER", "Khách Hai"),
                Account(8, "CUSTOMER", "Khách Ba"),
                // A suspended officer of ward 1 is not notified.
                Account(12, "WARD_AUTHORITY", "Cán bộ nghỉ", ward: 1, status: "SUSPENDED"));
            f.Db.Vendors.AddRange(new Vendor { vendor_id = 10, user_id = 1 }, new Vendor { vendor_id = 11, user_id = 6 });
            f.Db.PricingZones.AddRange(
                new PricingZone { zone_id = 1, ward_unit_id = 1, zone_name = "Khu Một", created_by = 3 },
                new PricingZone { zone_id = 2, ward_unit_id = 2, zone_name = "Khu Hai", created_by = 4 });
            f.Db.SidewalkSlots.AddRange(
                new SidewalkSlot { slot_id = 100, zone_id = 1, slot_code = "A-01", source = "WARD_DEFINED", slot_status = "ACTIVE", latitude = 16.06m, longitude = 108.21m },
                new SidewalkSlot { slot_id = 200, zone_id = 2, slot_code = "B-01", source = "WARD_DEFINED", slot_status = "ACTIVE", latitude = 16.07m, longitude = 108.22m });
            f.Db.BusinessRegistrations.AddRange(
                new BusinessRegistration { registration_id = 1, vendor_id = 10, ward_unit_id = 1, vendor_type = "ITINERANT", display_name = "Quầy A", registration_status = "APPROVED" },
                new BusinessRegistration { registration_id = 2, vendor_id = 11, ward_unit_id = 2, vendor_type = "ITINERANT", display_name = "Quầy B", registration_status = "APPROVED" });
            f.Db.RentalApplications.AddRange(
                new RentalApplication { application_id = 1, registration_id = 1, slot_id = 100, application_method = "MANUAL_SELECTED", application_status = "APPROVED" },
                new RentalApplication { application_id = 2, registration_id = 2, slot_id = 200, application_method = "MANUAL_SELECTED", application_status = "APPROVED" });
            f.Db.RentalContracts.AddRange(
                new RentalContract { contract_id = 1, application_id = 1, slot_id = 100, vendor_id = 10, start_date = new DateOnly(2026, 10, 1), end_date = new DateOnly(2026, 12, 31), contract_status = "ACTIVE" },
                new RentalContract { contract_id = 2, application_id = 2, slot_id = 200, vendor_id = 11, start_date = new DateOnly(2026, 10, 1), end_date = new DateOnly(2026, 12, 31), contract_status = "ACTIVE" });
            f.Db.Storefronts.AddRange(
                new Storefront { storefront_id = 1, registration_id = 1, contract_id = 1, storefront_name = "Quầy A", availability_status = "OPEN" },
                new Storefront { storefront_id = 2, registration_id = 2, contract_id = 2, storefront_name = "Quầy B", availability_status = "OPEN" });
            f.Db.DigitalPermits.AddRange(
                new DigitalPermit { permit_id = 1, contract_id = 1, qr_payload = f.Token1, permit_status = "ACTIVE", issued_at = T0 },
                new DigitalPermit { permit_id = 2, contract_id = 2, qr_payload = f.tokens.Create(2, T0), permit_status = "ACTIVE", issued_at = T0 });
            f.Db.FoodCategories.Add(new FoodCategory { category_id = 1, category_name = "Món chính", created_by = 5 });
            await f.Db.SaveChangesAsync();
            f.Db.ChangeTracker.Clear();
            return f;
        }

        public CommerceManagement Menu(string? publicScanBaseUrl = null, long vendorId = 10, bool tokens = false)
        {
            var vendor = new Mock<IVendorContext>();
            vendor.Setup(x => x.RequireVendorIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(vendorId);
            var storage = new Mock<IFileStorage>();
            storage.Setup(x => x.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
            return new CommerceManagement(Db, vendor.Object, Mock.Of<ICustomerContext>(), storage.Object, Clock,
                new PermitLinks(Options.Create(new PermitSettings { PublicScanBaseUrl = publicScanBaseUrl })),
                tokens ? this.tokens : null);
        }

        /// <summary>Business A (registration 1) rents one more slot; returns that slot's permit payload.</summary>
        public async Task<string> AddSlot(
            long slotId, string slotCode, long contractId, long permitId,
            string permitStatus = PermitStatuses.Active, string? payload = null)
        {
            payload ??= tokens.Create(contractId, T0);
            Db.SidewalkSlots.Add(new SidewalkSlot { slot_id = slotId, zone_id = 1, slot_code = slotCode, source = "WARD_DEFINED", slot_status = "ACTIVE", latitude = 16.06m, longitude = 108.21m });
            Db.RentalApplications.Add(new RentalApplication { application_id = contractId, registration_id = 1, slot_id = slotId, application_method = "MANUAL_SELECTED", application_status = "APPROVED" });
            Db.RentalContracts.Add(new RentalContract { contract_id = contractId, application_id = contractId, slot_id = slotId, vendor_id = 10, start_date = new DateOnly(2026, 10, 1), end_date = new DateOnly(2026, 12, 31), contract_status = "ACTIVE" });
            Db.DigitalPermits.Add(new DigitalPermit { permit_id = permitId, contract_id = contractId, qr_payload = payload, permit_status = permitStatus, issued_at = T0 });
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
            return payload;
        }

        public CommunityVendorRepository Community() => new(Db, Clock);

        public VendorReportDecisions Decisions() => new(Db, Clock);

        public AiMarketplaceRepository Inbox() => new(Db, new CommerceRepository(Db, Clock), Clock);

        public ReportSuspiciousVendorCommandHandler ReportHandler(long customerId)
        {
            var customer = new Mock<ICustomerContext>();
            customer.Setup(x => x.RequireCustomerUserIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(customerId);
            return new ReportSuspiciousVendorCommandHandler(customer.Object, Community(), new VendorReportSettings(), Clock);
        }

        public async Task<long> AddDish(string name, decimal price, string unit, string? nameEn = null)
        {
            var dish = await Menu().SaveMenu(1, null, new SellerMenuInput(1, name, null, price, "AVAILABLE", Photo, unit, nameEn), default);
            return dish.MenuItemId;
        }

        public async Task<List<MenuItemPriceHistory>> History(long dish) =>
            await Db.MenuItemPriceHistories.AsNoTracking().Where(h => h.menu_item_id == dish)
                .OrderBy(h => h.price_history_id).ToListAsync();

        /// <summary>A report on ward 1's slot A-01, about vendor 10 unless told otherwise.</summary>
        public async Task<long> SeedReport(
            long customerId,
            string type,
            decimal? charged = null,
            DateTime? dueAt = null,
            DateTime? created = null,
            string status = VendorReportStatuses.Pending,
            DateTime? reviewedAt = null,
            long vendorId = 10)
        {
            var closed = status != VendorReportStatuses.Pending;
            var report = new VendorReport
            {
                reporter_user_id = customerId, vendor_id = vendorId, slot_id = 100, report_reason = "Phản ánh",
                report_status = status, report_type = type, created_at = created ?? Clock.GetUtcNow().UtcDateTime,
                due_at = dueAt, charged_amount = charged, posted_amount = charged is null ? null : charged - 30_000,
                quantity = charged is null ? null : 2,
                resolution = closed ? VendorReportResolutions.Dismissed : null,
                resolution_note = closed ? "Lý do" : null,
                reviewed_by = closed ? 3 : null, reviewer_role = closed ? "WARD_AUTHORITY" : null,
                reviewed_at = closed ? reviewedAt ?? Clock.GetUtcNow().UtcDateTime : null,
            };
            Db.VendorReports.Add(report);
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
            return report.report_id;
        }

        public void Dispose()
        {
            Db.Dispose();
            connection.Dispose();
        }
    }

    private sealed class TestClock(DateTime start) : TimeProvider
    {
        private DateTime now = start;

        public void Advance(TimeSpan by) => now += by;

        public override DateTimeOffset GetUtcNow() => new(now, TimeSpan.Zero);
    }

    private sealed class TestContext(DbContextOptions<StreetBizDbContext> options) : StreetBizDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            foreach (var entity in modelBuilder.Model.GetEntityTypes())
                foreach (var property in entity.GetProperties())
                {
                    if (property.GetComputedColumnSql() is not null) { property.SetComputedColumnSql(null); property.ValueGenerated = ValueGenerated.Never; }
                    property.SetDefaultValueSql(null);
                    if (property.ClrType == typeof(decimal) || property.ClrType == typeof(decimal?))
                        modelBuilder.Entity(entity.ClrType).Property(property.Name).HasConversion<double>();
                }
        }
    }
}
