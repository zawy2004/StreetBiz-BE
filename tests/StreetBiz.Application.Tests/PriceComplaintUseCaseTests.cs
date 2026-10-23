using FluentAssertions;
using Moq;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Community;
using StreetBiz.Application.Features.CommunityVendors;

namespace StreetBiz.Application.Tests;

/// <summary>
/// BUY-06/07: the public price board behind a scanned QR and the customer's price complaint. The
/// rules of section 3 the application layer owns: live verdicts with the board only for a VALID
/// permit, the posted price taken from history and never from the client, at most one pending
/// report per person and vendor, and nothing about the reporter reaching the vendor.
/// </summary>
public sealed class PriceComplaintUseCaseTests
{
    private const string Token = "SBP1.signed.token";
    private const long ContractId = 9;
    private const long VendorId = 2;
    private const long CustomerId = 7;
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 5, 0, 0, TimeSpan.Zero); // Friday 12:00 in Đà Nẵng

    private readonly Mock<ICommunityVendorRepository> repository = new();
    private readonly Mock<IPermitTokenService> tokens = new();
    private readonly Mock<ICustomerContext> customer = new();

    public PriceComplaintUseCaseTests()
    {
        var claims = new PermitTokenClaims(ContractId, DateTime.UtcNow);
        tokens.Setup(x => x.TryParse(Token, out claims)).Returns(true);
        customer.Setup(x => x.RequireCustomerUserIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(CustomerId);
        repository.Setup(x => x.GetReportTargetAsync(VendorId, It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReportTargetRow(VendorId, "Bún chả Cô Lan", "A-01"));
        repository.Setup(x => x.ReportReferencesBelongToVendorAsync(VendorId, It.IsAny<long?>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
    }

    #region Public check (BUY-02 / BUY-06, BR-39)

    [Fact]
    public async Task A_link_is_verified_by_the_token_inside_it_and_the_scan_is_logged_as_scanned()
    {
        var link = $"http://192.168.1.5:5173/q/{Token}?v=3";
        Permit(PermitEffectiveStatuses.Valid);
        Board(version: 5);

        var result = await Verify(link);

        result.IsValid.Should().BeTrue();
        repository.Verify(x => x.GetPermitVerificationAsync(ContractId, Token, It.IsAny<CancellationToken>()));
        repository.Verify(x => x.AddPermitScanAsync(
            It.Is<PublicPermitScan>(scan => scan.QrPayload == link && scan.Result == PermitEffectiveStatuses.Valid),
            It.IsAny<CancellationToken>()));
        result.PriceBoard.Should().NotBeNull();
        result.PriceBoard!.Version.Should().Be(5);
        result.PriceBoard.Items.Should().ContainSingle().Which.PriceUnit.Should().Be("suất");
        // The paper board says bản 3, the live board is bản 5.
        result.PriceBoard.PrintedVersionIsCurrent.Should().BeFalse();
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(5, true)]
    [InlineData(4, false)]
    public async Task The_printed_version_is_compared_with_the_live_one(int? printed, bool? isCurrent)
    {
        Permit(PermitEffectiveStatuses.Valid);
        Board(version: 5);

        var result = await Verify(Token, printed);

        result.PriceBoard!.PrintedVersionIsCurrent.Should().Be(isCurrent);
    }

    [Theory]
    [InlineData(PermitEffectiveStatuses.Suspended)]
    [InlineData(PermitEffectiveStatuses.Expired)]
    [InlineData(PermitEffectiveStatuses.Revoked)]
    [InlineData(PermitEffectiveStatuses.NotYetValid)]
    public async Task No_price_board_is_shown_unless_the_permit_is_valid(string status)
    {
        Permit(status);
        Board(version: 5);

        var result = await Verify(Token);

        result.IsValid.Should().BeFalse();
        result.Status.Should().Be(status);
        result.PriceBoard.Should().BeNull();
        repository.Verify(x => x.GetPriceBoardByContractAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
        repository.Verify(x => x.AddPermitScanAsync(It.IsAny<PublicPermitScan>(), It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task A_tampered_link_is_not_valid_and_nothing_is_looked_up_but_the_scan_is_still_logged()
    {
        var tampered = $"https://streetbiz.vn/q/{Token}x";

        var result = await Verify(tampered);

        result.IsValid.Should().BeFalse();
        result.Status.Should().Be(PermitScanResults.NotFound);
        result.PriceBoard.Should().BeNull();
        repository.Verify(x => x.GetPermitVerificationAsync(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        repository.Verify(x => x.AddPermitScanAsync(
            It.Is<PublicPermitScan>(scan => scan.PermitId == null && scan.Result == PermitScanResults.NotFound),
            It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task Every_check_reads_the_server_again_nothing_is_reused()
    {
        Permit(PermitEffectiveStatuses.Valid);
        Board(version: 5);

        await Verify(Token);
        await Verify(Token);

        repository.Verify(x => x.GetPermitVerificationAsync(ContractId, Token, It.IsAny<CancellationToken>()), Times.Exactly(2));
        repository.Verify(x => x.GetPriceBoardByContractAsync(ContractId, It.IsAny<CancellationToken>()), Times.Exactly(2));
        repository.Verify(x => x.AddPermitScanAsync(It.IsAny<PublicPermitScan>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    #endregion

    #region Complaint (BUY-05 / BUY-07)

    [Fact]
    public async Task An_overcharge_is_judged_against_the_posted_price_at_purchase_times_the_quantity()
    {
        var purchasedAt = Now.AddHours(-3);
        Posted(purchasedAt.UtcDateTime, unitPrice: 45_000);
        CustomerVendorReport? saved = null;
        NewVendorReportNotices? notices = null;
        repository.Setup(x => x.AddReportAsync(VendorId, CustomerId, It.IsAny<CustomerVendorReport>(),
                It.IsAny<NewVendorReportNotices>(), It.IsAny<CancellationToken>()))
            .Callback<long, long, CustomerVendorReport, NewVendorReportNotices, CancellationToken>((_, _, r, n, _) => (saved, notices) = (r, n))
            .ReturnsAsync(41);

        var receipt = await Report(Overcharge(quantity: 2, charged: 120_000, purchasedAt));

        receipt.ReportId.Should().Be(41);
        receipt.PostedAmount.Should().Be(90_000);
        saved!.ReportType.Should().Be(VendorReportTypes.Overcharge);
        saved.PriceHistoryId.Should().Be(77);
        saved.PostedAmount.Should().Be(90_000);
        saved.ChargedAmount.Should().Be(120_000);
        saved.PurchasedAt.Should().Be(purchasedAt.UtcDateTime);
        saved.MenuItemId.Should().Be(12);
        // Friday 23/10/2026 + 7 working days = Tuesday 03/11, closing at the end of that Vietnamese day.
        saved.DueAt.Should().Be(new DateTime(2026, 11, 3, 17, 0, 0, DateTimeKind.Utc));
        notices!.ForWard.Body.Should().Contain("Bán cao hơn giá niêm yết").And.Contain("03/11/2026");
        notices.ForVendor.Body.Should().Contain("120.000đ").And.Contain("90.000đ");
    }

    [Fact]
    public async Task The_price_looked_up_is_the_one_in_force_at_the_purchase_time_not_now()
    {
        var purchasedAt = Now.AddDays(-2);
        Posted(purchasedAt.UtcDateTime, unitPrice: 40_000);

        await Report(Overcharge(quantity: 1, charged: 45_000, purchasedAt));

        repository.Verify(x => x.FindPostedPriceAsync(VendorId, 12, purchasedAt.UtcDateTime, It.IsAny<CancellationToken>()));
        repository.Verify(x => x.AddReportAsync(VendorId, CustomerId,
            It.Is<CustomerVendorReport>(r => r.PostedAmount == 40_000), It.IsAny<NewVendorReportNotices>(), It.IsAny<CancellationToken>()));
    }

    [Theory]
    [InlineData(90_000)]
    [InlineData(80_000)]
    public async Task A_charge_that_is_not_above_the_posted_price_is_not_an_overcharge(int charged)
    {
        Posted(Now.AddHours(-1).UtcDateTime, unitPrice: 45_000);

        var act = () => Report(Overcharge(quantity: 2, charged: charged, Now.AddHours(-1)));

        await act.Should().ThrowAsync<DomainRuleException>().WithMessage(CommunityMessages.NotAbovePostedPrice);
        NothingSaved();
    }

    [Fact]
    public async Task A_dish_that_was_not_on_the_board_at_purchase_cannot_be_complained_about()
    {
        repository.Setup(x => x.FindPostedPriceAsync(VendorId, 12, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PostedPriceRow(12, 1, "Bún chả Cô Lan", "A-01", 80, "Bún chả", "suất", 45_000, IsListed: false, Now.UtcDateTime));

        var act = () => Report(Overcharge(quantity: 1, charged: 60_000, Now.AddHours(-1)));

        await act.Should().ThrowAsync<DomainRuleException>().WithMessage(CommunityMessages.NotListedAtPurchase);
        NothingSaved();
    }

    [Fact]
    public async Task Another_vendors_dish_is_not_found()
    {
        repository.Setup(x => x.FindPostedPriceAsync(VendorId, 12, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PostedPriceRow?)null);

        var act = () => Report(Overcharge(quantity: 1, charged: 60_000, Now.AddHours(-1)));

        await act.Should().ThrowAsync<NotFoundException>().WithMessage(CommunityMessages.MenuItemNotFound);
    }

    [Theory]
    [InlineData(-7 * 24 - 1, CommunityMessages.PurchaseTooOld)]
    [InlineData(10, CommunityMessages.PurchaseInFuture)]
    public async Task The_purchase_must_be_within_seven_days_and_not_in_the_future(int hoursFromNow, string message)
    {
        Posted(Now.UtcDateTime, unitPrice: 45_000);

        var act = () => Report(Overcharge(quantity: 1, charged: 60_000, Now.AddHours(hoursFromNow)));

        await act.Should().ThrowAsync<DomainRuleException>().WithMessage(message);
        NothingSaved();
    }

    [Fact]
    public async Task A_phone_clock_a_minute_ahead_counts_as_now()
    {
        Posted(Now.UtcDateTime, unitPrice: 45_000);

        await Report(Overcharge(quantity: 1, charged: 60_000, Now.AddMinutes(1)));

        repository.Verify(x => x.AddReportAsync(VendorId, CustomerId,
            It.Is<CustomerVendorReport>(r => r.PurchasedAt == Now.UtcDateTime), It.IsAny<NewVendorReportNotices>(), It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task A_second_pending_report_about_the_same_vendor_is_a_conflict()
    {
        repository.Setup(x => x.HasPendingReportAsync(CustomerId, VendorId, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var act = () => Report(new ReportSuspiciousVendorCommand(VendorId, "Lại lấn vỉa hè", null, null, null));

        (await act.Should().ThrowAsync<ConflictException>()).Which.StatusCode.Should().Be(409);
        NothingSaved();
    }

    [Fact]
    public async Task The_vendor_is_never_told_who_reported_nor_what_they_wrote()
    {
        Posted(Now.AddHours(-1).UtcDateTime, unitPrice: 45_000);
        NewVendorReportNotices? notices = null;
        repository.Setup(x => x.AddReportAsync(VendorId, CustomerId, It.IsAny<CustomerVendorReport>(),
                It.IsAny<NewVendorReportNotices>(), It.IsAny<CancellationToken>()))
            .Callback<long, long, CustomerVendorReport, NewVendorReportNotices, CancellationToken>((_, _, _, n, _) => notices = n)
            .ReturnsAsync(41);

        await Report(Overcharge(quantity: 2, charged: 120_000, Now.AddHours(-1),
            reason: "Tôi là Nguyễn Khách Hàng, gọi 0905000201 nếu cần."));

        notices!.ForVendor.Title.Should().NotContain("Nguyễn").And.NotContain("0905000201");
        notices.ForVendor.Body.Should().NotContain("Nguyễn").And.NotContain("0905000201").And.NotContain("Tôi là");
    }

    [Fact]
    public async Task A_report_may_be_filed_while_the_permit_is_not_valid()
    {
        // GetReportTargetAsync, unlike the public profile, does not require a VALID permit: an invalid
        // permit is exactly when the public page offers the report button.
        repository.Setup(x => x.AddReportAsync(VendorId, CustomerId, It.IsAny<CustomerVendorReport>(),
                It.IsAny<NewVendorReportNotices>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(42);

        var receipt = await Report(new ReportSuspiciousVendorCommand(VendorId, null, null, null, 3, VendorReportTypes.PriceNotPosted));

        receipt.ReportId.Should().Be(42);
        repository.Verify(x => x.GetPublicProfileAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
        repository.Verify(x => x.AddReportAsync(VendorId, CustomerId,
            It.Is<CustomerVendorReport>(r => r.ReportType == VendorReportTypes.PriceNotPosted && r.Reason == "Quầy không treo bảng giá."
                && r.PostedAmount == null && r.MenuItemId == null),
            It.IsAny<NewVendorReportNotices>(), It.IsAny<CancellationToken>()));
    }

    [Theory]
    [InlineData(VendorReportTypes.General, "Lấn vỉa hè", null, null, null, false, true)]
    [InlineData(VendorReportTypes.General, "", null, null, null, false, false)]
    [InlineData(VendorReportTypes.PriceNotPosted, null, null, null, null, false, true)]
    [InlineData(VendorReportTypes.PriceNotPosted, null, 12L, null, null, false, false)]
    [InlineData(VendorReportTypes.Overcharge, null, 12L, 2, 120_000.0, true, true)]
    [InlineData(VendorReportTypes.Overcharge, null, null, 2, 120_000.0, true, false)]
    [InlineData(VendorReportTypes.Overcharge, null, 12L, 0, 120_000.0, true, false)]
    [InlineData(VendorReportTypes.Overcharge, null, 12L, 2, 120_000.5, true, false)]
    [InlineData(VendorReportTypes.Overcharge, null, 12L, 2, 120_000.0, false, false)]
    [InlineData("SCORE", "x", null, null, null, false, false)]
    public void The_report_request_is_validated(
        string type, string? reason, long? menuItemId, int? quantity, double? charged, bool purchased, bool valid) =>
        new ReportSuspiciousVendorCommandValidator().Validate(new ReportSuspiciousVendorCommand(
                VendorId, reason, null, null, null, type, menuItemId, quantity, (decimal?)charged, purchased ? Now : null))
            .IsValid.Should().Be(valid);

    [Fact]
    public async Task My_reports_show_the_outcome_the_due_time_and_whether_it_is_overdue()
    {
        repository.Setup(x => x.ListMyReportsAsync(CustomerId, 1, ListMyVendorReportsQueryHandler.PageSize, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new MyVendorReportRow(1, VendorId, "Bún chả Cô Lan", "A-01", VendorReportTypes.Overcharge, "x", VendorReportStatuses.Reviewed,
                    Now.UtcDateTime.AddDays(-5), Now.UtcDateTime.AddDays(-1), VendorReportResolutions.Mediated, "Đã hoàn tiền", 30_000,
                    Now.UtcDateTime.AddDays(-4), false,
                    new VendorReportPriceEvidenceRow(12, "Bún chả", "suất", 45_000, 3, Now.UtcDateTime.AddDays(-20), 2, 90_000, 120_000, Now.UtcDateTime.AddDays(-5))),
                new MyVendorReportRow(2, VendorId, "Bún chả Cô Lan", null, VendorReportTypes.General, "y", VendorReportStatuses.Pending,
                    Now.UtcDateTime.AddDays(-12), Now.UtcDateTime.AddDays(-3), null, null, null, null, true, null),
            ]);

        var reports = await new ListMyVendorReportsQueryHandler(customer.Object, repository.Object, new FixedClock(Now))
            .Handle(new ListMyVendorReportsQuery(), default);

        reports.Should().HaveCount(2);
        reports[0].IsOverdue.Should().BeFalse("a closed report is never overdue");
        reports[0].RefundAmount.Should().Be(30_000);
        reports[0].PriceEvidence!.Difference.Should().Be(30_000);
        reports[1].IsOverdue.Should().BeTrue();
        reports[1].DueAt!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }

    #endregion

    #region Rules

    [Fact]
    public void The_due_date_skips_weekends_and_public_holidays()
    {
        // Thursday 27/08/2026: 31/08 and 01-02/09 are days off (Quốc khánh), so 7 working days end on
        // Thursday 10/09, not Monday 07/09.
        VendorReportRules.DueAt(new DateOnly(2026, 8, 27), 7)
            .Should().Be(new DateTime(2026, 9, 10, 17, 0, 0, DateTimeKind.Utc));
        VendorReportRules.DueAt(new DateOnly(2026, 10, 23), 7)
            .Should().Be(new DateTime(2026, 11, 3, 17, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Only_a_pending_report_past_its_due_time_is_overdue()
    {
        var now = Now.UtcDateTime;
        VendorReportRules.IsOverdue(VendorReportStatuses.Pending, now.AddMinutes(-1), now).Should().BeTrue();
        VendorReportRules.IsOverdue(VendorReportStatuses.Pending, now.AddMinutes(1), now).Should().BeFalse();
        VendorReportRules.IsOverdue(VendorReportStatuses.Pending, null, now).Should().BeFalse();
        VendorReportRules.IsOverdue(VendorReportStatuses.Dismissed, now.AddDays(-9), now).Should().BeFalse();
    }

    [Fact]
    public void Notices_carry_the_outcome_to_the_reporter_and_only_the_outcome_to_the_vendor()
    {
        const string note = "Đã gọi chị Hà 0905000202, người bán hoàn tiền tại quầy.";
        var subject = VendorReportNotices.Subject("Bún chả Cô Lan", "A-01");

        var reporter = VendorReportNotices.ResolvedForReporter(VendorReportResolutions.Mediated, subject, note, 30_000);
        var vendor = VendorReportNotices.ResolvedForVendor(VendorReportResolutions.Mediated, subject, 30_000);

        reporter.Body.Should().Contain(note).And.Contain("30.000đ");
        vendor.Body.Should().Contain("30.000đ").And.NotContain("0905000202").And.NotContain("chị Hà");
        VendorReportNotices.Subject(null, "B-02").Should().Be("quầy tại ô B-02");
    }

    #endregion

    private void Permit(string status) =>
        repository.Setup(x => x.GetPermitVerificationAsync(ContractId, Token, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PublicPermitVerificationRow(
                3, ContractId, VendorId, "Bún chả Cô Lan", 4, "A-01", 16, 108,
                new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), status));

    private void Board(int version) =>
        repository.Setup(x => x.GetPriceBoardByContractAsync(ContractId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PriceBoardRow(1, "Bún chả Cô Lan", "A-01", ContractId, version, Now.UtcDateTime,
                [new PriceBoardItemRow(12, "Bún chả", "Grilled pork noodles", "suất", 45_000)]));

    private void Posted(DateTime at, decimal unitPrice) =>
        repository.Setup(x => x.FindPostedPriceAsync(VendorId, 12, at, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PostedPriceRow(12, 1, "Bún chả Cô Lan", "A-01", 77, "Bún chả", "suất", unitPrice, true, at.AddDays(-10)));

    private void NothingSaved() =>
        repository.Verify(x => x.AddReportAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CustomerVendorReport>(),
            It.IsAny<NewVendorReportNotices>(), It.IsAny<CancellationToken>()), Times.Never);

    private static ReportSuspiciousVendorCommand Overcharge(int quantity, decimal charged, DateTimeOffset purchasedAt, string? reason = null) =>
        new(VendorId, reason, null, null, null, VendorReportTypes.Overcharge, 12, quantity, charged, purchasedAt);

    private Task<PermitVerificationDto> Verify(string payload, int? printed = null) =>
        new VerifyPublicPermitCommandHandler(repository.Object, tokens.Object, Mock.Of<ICurrentUser>())
            .Handle(new VerifyPublicPermitCommand(payload, null, null, null, printed), default);

    private Task<VendorReportReceiptDto> Report(ReportSuspiciousVendorCommand command) =>
        new ReportSuspiciousVendorCommandHandler(customer.Object, repository.Object, new VendorReportSettings(), new FixedClock(Now))
            .Handle(command, default);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
