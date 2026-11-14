using FluentAssertions;
using Moq;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.Tax;
using StreetBiz.Application.Features.WardSlots;

namespace StreetBiz.Application.Tests;

/// <summary>Household tax use cases against a faked repository: what each caller may do, and what the ward may not see.</summary>
public sealed class TaxHandlerTests
{
    private const long VendorId = 4;
    private const long UserId = 5;

    private static readonly DateTimeOffset Oct18 = new(2026, 10, 18, 3, 0, 0, TimeSpan.Zero); // 10:00 in Đà Nẵng
    private static readonly TaxPolicyRow Policy = TaxRulesTests.Policy();

    private static TimeProvider Clock(DateTimeOffset? now = null)
    {
        var clock = new Mock<TimeProvider>();
        clock.Setup(x => x.GetUtcNow()).Returns(now ?? Oct18);
        return clock.Object;
    }

    private static IVendorContext VendorContext()
    {
        var context = new Mock<IVendorContext>();
        context.Setup(x => x.RequireVendorIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(VendorId);
        return context.Object;
    }

    private static ICurrentUser CurrentUser()
    {
        var user = new Mock<ICurrentUser>();
        user.SetupGet(x => x.UserId).Returns(UserId);
        return user.Object;
    }

    private static IWardActorContext WardContext()
    {
        var context = new Mock<IWardActorContext>();
        context.Setup(x => x.RequireAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new WardActor(2, 10, "Cán bộ Vân"));
        return context.Object;
    }

    private static Mock<ITaxRepository> Repository(params RevenueDayRow[] days)
    {
        var repository = new Mock<ITaxRepository>();
        repository.Setup(x => x.GetPolicyAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(Policy);
        repository.Setup(x => x.GetProfileAsync(VendorId, It.IsAny<CancellationToken>())).ReturnsAsync((VendorTaxProfileRow?)null);
        repository.Setup(x => x.GetDailyRevenueAsync(VendorId, It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(days);
        repository.Setup(x => x.ListDeclarationsAsync(VendorId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<TaxDeclarationRow>());
        return repository;
    }

    private static RevenueDayRow Day(string date, decimal recorded, decimal online = 0, int orders = 0) =>
        new(DateOnly.Parse(date), online, orders, recorded);

    // ------------------------------------------------------------------------------ overview

    [Fact]
    public async Task A_household_near_the_threshold_is_warned_with_a_crossing_date_and_the_duties_that_follow()
    {
        var repository = Repository(Day("2026-03-01", recorded: 600_000_000m), Day("2026-09-01", recorded: 250_000_000m, online: 0));

        var overview = await new GetTaxOverviewQueryHandler(VendorContext(), repository.Object, Clock())
            .Handle(new GetTaxOverviewQuery(null), CancellationToken.None);

        overview.TaxYear.Should().Be(2026);
        overview.Revenue.Total.Should().Be(850_000_000m);
        overview.Group.Should().Be("G1");
        overview.Level.Should().Be("NEAR");
        overview.ThresholdRatio.Should().Be(0.85m);
        overview.RemainingToThreshold.Should().Be(150_000_000m);
        overview.Projection.Enough.Should().BeTrue();
        overview.Projection.WillCross.Should().BeTrue();
        overview.Projection.CrossingDate.Should().NotBeNull();
        overview.Projection.ProjectedLevel.Should().Be("OVER");
        overview.ObligationsOnceOver.Select(o => o.Code).Should().Contain("EINVOICE");
        overview.NextDeadline.Should().NotBeNull();
        overview.NextDeadline!.PeriodType.Should().Be("YEAR");
        overview.NextDeadline.DueDate.Should().Be(new DateOnly(2027, 1, 31));
        overview.NextDeadline.Status.Should().Be("NOT_YET");
        overview.Months.Should().HaveCount(12);
        overview.Months[2].Recorded.Should().Be(600_000_000m);
        overview.Months[8].Total.Should().Be(250_000_000m);
        overview.Disclaimer.Should().Contain("không khai");
    }

    [Fact]
    public async Task A_household_over_the_threshold_must_declare_the_quarters_it_has_crossed()
    {
        // 1.2 tỷ by 1 March: every quarter from Q1 on is above the threshold.
        var repository = Repository(Day("2026-03-01", recorded: 1_200_000_000m));
        var clock = Clock(new DateTimeOffset(2027, 1, 20, 3, 0, 0, TimeSpan.Zero));

        var declarations = await new ListTaxDeclarationsQueryHandler(VendorContext(), repository.Object, clock)
            .Handle(new ListTaxDeclarationsQuery(2026), CancellationToken.None);

        declarations.Group.Should().Be("G2");
        declarations.Periods.Single(p => p.PeriodType == "YEAR").Status.Should().Be("DUE_SOON");
        var quarters = declarations.Periods.Where(p => p.PeriodType == "QUARTER").ToList();
        quarters.Should().OnlyContain(q => q.Required);
        quarters.Single(q => q.PeriodNo == 1).SystemRevenue.Should().Be(1_200_000_000m);
        quarters.Single(q => q.PeriodNo == 2).SystemRevenue.Should().Be(0);
        quarters.Single(q => q.PeriodNo == 1).Status.Should().Be("OVERDUE");
    }

    [Fact]
    public async Task A_household_under_the_threshold_only_has_the_yearly_notice()
    {
        var repository = Repository(Day("2026-03-01", recorded: 300_000_000m));
        var clock = Clock(new DateTimeOffset(2027, 1, 20, 3, 0, 0, TimeSpan.Zero));

        var declarations = await new ListTaxDeclarationsQueryHandler(VendorContext(), repository.Object, clock)
            .Handle(new ListTaxDeclarationsQuery(2026), CancellationToken.None);

        declarations.Periods.Where(p => p.PeriodType == "QUARTER").Should().OnlyContain(q => !q.Required && q.Status == "NOT_REQUIRED");
        declarations.Periods.Single(p => p.PeriodType == "YEAR").Required.Should().BeTrue();
    }

    [Fact]
    public async Task A_year_that_has_not_started_has_no_overview()
    {
        var act = () => new GetTaxOverviewQueryHandler(VendorContext(), Repository().Object, Clock())
            .Handle(new GetTaxOverviewQuery(2027), CancellationToken.None);

        await act.Should().ThrowAsync<DomainRuleException>().WithMessage("*Chưa đến năm*");
    }

    [Fact]
    public async Task Without_a_published_policy_nothing_can_be_computed()
    {
        var repository = Repository();
        repository.Setup(x => x.GetPolicyAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync((TaxPolicyRow?)null);

        var act = () => new GetTaxOverviewQueryHandler(VendorContext(), repository.Object, Clock())
            .Handle(new GetTaxOverviewQuery(null), CancellationToken.None);

        await act.Should().ThrowAsync<DomainRuleException>().WithMessage("*Chưa có chính sách*");
    }

    // ----------------------------------------------------------------------- revenue entries

    [Theory]
    [InlineData("2026-10-19")] // tomorrow
    [InlineData("2025-12-31")] // before the first tax day
    public async Task An_entry_must_be_a_day_from_the_first_tax_day_to_today(string date)
    {
        var repository = Repository();

        var act = () => new AddRevenueEntryCommandHandler(VendorContext(), repository.Object, Clock())
            .Handle(new AddRevenueEntryCommand(new AddRevenueEntryRequest(DateOnly.Parse(date), 100_000m, "CASH", null)), CancellationToken.None);

        await act.Should().ThrowAsync<DomainRuleException>();
        repository.Verify(x => x.AddEntryAsync(It.IsAny<long>(), It.IsAny<DateOnly>(), It.IsAny<decimal>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_valid_entry_is_recorded_for_the_caller_with_a_trimmed_note()
    {
        var repository = Repository();
        repository.Setup(x => x.AddEntryAsync(VendorId, new DateOnly(2026, 10, 17), 2_500_000m, "CASH", "Bán cả ngày",
                It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RevenueEntryRow(9, new DateOnly(2026, 10, 17), 2_500_000m, "CASH", "Bán cả ngày", Oct18.UtcDateTime, null, null));

        var entry = await new AddRevenueEntryCommandHandler(VendorContext(), repository.Object, Clock())
            .Handle(new AddRevenueEntryCommand(new AddRevenueEntryRequest(new DateOnly(2026, 10, 17), 2_500_000m, "CASH", "  Bán cả ngày ")),
                CancellationToken.None);

        entry.EntryId.Should().Be(9);
        entry.Voided.Should().BeFalse();
    }

    [Theory]
    [InlineData(0, "CASH", false)]
    [InlineData(-5, "CASH", false)]
    [InlineData(1000.5, "CASH", false)] // no fractions of a dong
    [InlineData(10_000_000_001, "CASH", false)]
    [InlineData(100_000, "CARD", false)]
    [InlineData(100_000, "BANK_TRANSFER", true)]
    [InlineData(100_000, "EWALLET", true)]
    public void An_entry_needs_a_whole_positive_amount_and_a_known_channel(double amount, string channel, bool valid)
    {
        var result = new AddRevenueEntryCommandValidator()
            .Validate(new AddRevenueEntryCommand(new AddRevenueEntryRequest(new DateOnly(2026, 10, 17), (decimal)amount, channel, null)));

        result.IsValid.Should().Be(valid);
    }

    [Fact]
    public async Task Voiding_an_entry_that_is_not_the_callers_is_not_found()
    {
        var repository = Repository();
        repository.Setup(x => x.VoidEntryAsync(VendorId, UserId, 77, null, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((RevenueEntryRow?)null);

        var act = () => new VoidRevenueEntryCommandHandler(VendorContext(), CurrentUser(), repository.Object, Clock())
            .Handle(new VoidRevenueEntryCommand(77, "  "), CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    // ----------------------------------------------------------------------------- profile

    [Fact]
    public async Task A_profile_with_an_industry_the_policy_does_not_know_is_refused()
    {
        var act = () => new UpdateTaxProfileCommandHandler(VendorContext(), CurrentUser(), Repository().Object, Clock())
            .Handle(new UpdateTaxProfileCommand(new UpdateTaxProfileRequest(null, "SPACE_TOURISM", false, false)), CancellationToken.None);

        await act.Should().ThrowAsync<DomainRuleException>().WithMessage("*Ngành nghề*");
    }

    [Fact]
    public async Task A_profile_is_saved_with_the_callers_ids_and_the_sharing_choice()
    {
        var repository = Repository();
        repository.Setup(x => x.SaveProfileAsync(VendorId, UserId, "048090001234", "FOOD_SERVICE", true, true,
                It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VendorTaxProfileRow(VendorId, "048090001234", "FOOD_SERVICE", true, true, Oct18.UtcDateTime));

        var profile = await new UpdateTaxProfileCommandHandler(VendorContext(), CurrentUser(), repository.Object, Clock())
            .Handle(new UpdateTaxProfileCommand(new UpdateTaxProfileRequest(" 048090001234 ", "FOOD_SERVICE", true, true)), CancellationToken.None);

        profile.ShareRevenueWithWard.Should().BeTrue();
        profile.IndustryName.Should().Be("Ăn uống");
        profile.TaxCode.Should().Be("048090001234");
    }

    [Theory]
    [InlineData("12345", false)]
    [InlineData("048090001234", true)]
    [InlineData("", true)] // no tax code yet is allowed
    public void A_tax_code_that_is_given_must_have_a_valid_shape(string code, bool valid) =>
        new UpdateTaxProfileCommandValidator()
            .Validate(new UpdateTaxProfileCommand(new UpdateTaxProfileRequest(code, "FOOD_SERVICE", false, false)))
            .IsValid.Should().Be(valid);

    // ---------------------------------------------------------------------------- estimate

    [Fact]
    public async Task The_projected_estimate_uses_the_forecast_when_there_is_enough_data()
    {
        var repository = Repository(Day("2026-01-02", recorded: 900_000_000m));
        // 900 triệu in 289 days is ~3,1 triệu a day: carried over the last 74 days it ends near 1,13 tỷ.

        var estimate = await new GetTaxEstimateQueryHandler(VendorContext(), repository.Object, Clock())
            .Handle(new GetTaxEstimateQuery(null, "PROJECTED", null, null), CancellationToken.None);

        estimate.Basis.Should().Be("PROJECTED");
        estimate.Revenue.Should().BeGreaterThan(1_000_000_000m);
        estimate.Taxable.Should().BeTrue();
        estimate.Vat.Should().Be(decimal.Round(estimate.Revenue * 0.03m, 0, MidpointRounding.AwayFromZero));
    }

    [Fact]
    public async Task A_what_if_revenue_overrides_the_book()
    {
        var estimate = await new GetTaxEstimateQueryHandler(VendorContext(), Repository(Day("2026-03-01", recorded: 1_000_000m)).Object, Clock())
            .Handle(new GetTaxEstimateQuery(null, null, 2_000_000_000m, 0.98m), CancellationToken.None);

        estimate.Basis.Should().Be("CUSTOM");
        estimate.TotalOnRevenue.Should().Be(75_000_000m);
        estimate.Cheaper.Should().Be("PROFIT");
        estimate.Disclaimer.Should().NotBeNullOrWhiteSpace();
    }

    // -------------------------------------------------------------------------------- filings

    private static readonly DateTimeOffset Feb1 = new(2027, 2, 1, 3, 0, 0, TimeSpan.Zero);

    private static ConfirmTaxFilingCommand Confirm(string type, int no, string filedOn, string? receipt = null) =>
        new(2026, type, no, new ConfirmTaxFilingRequest(900_000_000m, DateOnly.Parse(filedOn), "BN-0001", receipt, null));

    [Fact]
    public async Task A_period_that_has_not_ended_cannot_be_confirmed()
    {
        var act = () => new ConfirmTaxFilingCommandHandler(VendorContext(), CurrentUser(), Repository().Object, Clock())
            .Handle(Confirm("YEAR", 0, "2026-10-18"), CancellationToken.None);

        await act.Should().ThrowAsync<DomainRuleException>().WithMessage("*chưa kết thúc*");
    }

    [Fact]
    public async Task A_quarter_below_the_threshold_is_not_a_declaration()
    {
        var repository = Repository(Day("2026-08-01", recorded: 300_000_000m));

        var act = () => new ConfirmTaxFilingCommandHandler(VendorContext(), CurrentUser(), repository.Object, Clock(Feb1))
            .Handle(Confirm("QUARTER", 3, "2026-10-20"), CancellationToken.None);

        await act.Should().ThrowAsync<DomainRuleException>().WithMessage("*không thuộc diện phải khai*");
    }

    [Theory]
    [InlineData("2026-12-31")] // not after the period ended
    [InlineData("2027-02-02")] // in the future
    public async Task A_filing_date_must_be_after_the_period_and_not_in_the_future(string filedOn)
    {
        var act = () => new ConfirmTaxFilingCommandHandler(VendorContext(), CurrentUser(), Repository().Object, Clock(Feb1))
            .Handle(Confirm("YEAR", 0, filedOn), CancellationToken.None);

        await act.Should().ThrowAsync<DomainRuleException>().WithMessage("*Ngày nộp*");
    }

    [Fact]
    public async Task A_receipt_file_that_belongs_to_someone_else_is_refused()
    {
        var foreign = EvidenceFiles.BuildUrl(99, EvidenceFiles.NewFileName(".pdf"));

        var act = () => new ConfirmTaxFilingCommandHandler(VendorContext(), CurrentUser(), Repository().Object, Clock(Feb1))
            .Handle(Confirm("YEAR", 0, "2027-01-20", foreign), CancellationToken.None);

        await act.Should().ThrowAsync<DomainRuleException>().WithMessage("*biên nhận*");
    }

    [Fact]
    public async Task A_confirmed_filing_keeps_the_figure_StreetBiz_had_beside_the_one_declared()
    {
        var receipt = EvidenceFiles.BuildUrl(UserId, EvidenceFiles.NewFileName(".pdf"));
        var repository = Repository(Day("2026-03-01", recorded: 700_000_000m), Day("2026-06-01", recorded: 150_000_000m, online: 50_000_000m, orders: 4));
        SaveTaxDeclaration? saved = null;
        repository.Setup(x => x.SaveDeclarationAsync(VendorId, UserId, It.IsAny<SaveTaxDeclaration>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Callback<long, long, SaveTaxDeclaration, DateTime, CancellationToken>((_, _, declaration, _, _) => saved = declaration)
            .ReturnsAsync(new TaxDeclarationRow(1, 2026, "YEAR", 0, 900_000_000m, 900_000_000m, new DateOnly(2027, 1, 20), "BN-0001", receipt, null, Feb1.UtcDateTime, null));
        repository.SetupSequence(x => x.ListDeclarationsAsync(VendorId, 2026, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new TaxDeclarationRow(1, 2026, "YEAR", 0, 900_000_000m, 900_000_000m, new DateOnly(2027, 1, 20), "BN-0001", receipt, null, Feb1.UtcDateTime, null)]);

        var period = await new ConfirmTaxFilingCommandHandler(VendorContext(), CurrentUser(), repository.Object, Clock(Feb1))
            .Handle(Confirm("YEAR", 0, "2027-01-20", receipt), CancellationToken.None);

        saved.Should().NotBeNull();
        saved!.RevenueSystem.Should().Be(900_000_000m, "online 50 triệu + recorded 850 triệu");
        saved.RevenueDeclared.Should().Be(900_000_000m);
        saved.ReceiptUrl.Should().Be(receipt);
        period.Status.Should().Be("FILED");
        period.Filing.Should().NotBeNull();
    }

    [Fact]
    public async Task Retracting_a_filing_that_does_not_exist_is_not_found()
    {
        var repository = Repository();
        repository.Setup(x => x.DeleteDeclarationAsync(VendorId, UserId, 2026, "YEAR", 0, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var act = () => new DeleteTaxFilingCommandHandler(VendorContext(), CurrentUser(), repository.Object, Clock())
            .Handle(new DeleteTaxFilingCommand(2026, "YEAR", 0), CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    // ------------------------------------------------------------------------------ exports

    [Fact]
    public async Task The_revenue_book_export_carries_the_household_its_months_and_its_entries()
    {
        var repository = Repository(Day("2026-03-01", recorded: 600_000_000m, online: 10_000_000m, orders: 3));
        repository.Setup(x => x.GetVendorIdentityAsync(VendorId, It.IsAny<CancellationToken>())).ReturnsAsync(("Phạm Thị Lan", "Bánh mì Cô Lan"));
        repository.Setup(x => x.ListEntriesAsync(VendorId, It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new RevenueEntryRow(1, new DateOnly(2026, 3, 1), 600_000_000m, "CASH", null, Oct18.UtcDateTime, null, null),
                new RevenueEntryRow(2, new DateOnly(2026, 3, 1), 5_000m, "OTHER", "nhập nhầm", Oct18.UtcDateTime, Oct18.UtcDateTime, "Nhập nhầm"),
            ]);
        TaxBookExport? exported = null;
        var renderer = new Mock<ITaxDocumentRenderer>();
        renderer.Setup(x => x.RenderRevenueBookXlsx(It.IsAny<TaxBookExport>()))
            .Callback<TaxBookExport>(book => exported = book)
            .Returns([1, 2, 3]);

        var file = await new ExportRevenueBookQueryHandler(VendorContext(), repository.Object, renderer.Object, Clock())
            .Handle(new ExportRevenueBookQuery(null), CancellationToken.None);

        file.FileName.Should().Be("so-doanh-thu-2026.xlsx");
        file.ContentType.Should().Contain("spreadsheetml");
        exported!.VendorName.Should().Be("Phạm Thị Lan");
        exported.BusinessName.Should().Be("Bánh mì Cô Lan");
        exported.Total.Should().Be(610_000_000m);
        exported.Months[2].Online.Should().Be(10_000_000m);
        exported.Days.Single().Remarks.Should().Contain("3 đơn hàng StreetBiz").And.Contain("Tiền mặt");
        exported.Entries.Should().HaveCount(2);
        exported.Entries[1].Voided.Should().BeTrue();
    }

    // ------------------------------------------------------------------------- ward: privacy

    private static WardHouseholdFacts Household(long vendorId, string name, bool shares, string? taxCode) => new(
        vendorId, 100 + vendorId, name, "Chủ " + name, "0905000100", "Nguyễn Văn Linh", "Ăn uống", "FIXED_STOREFRONT",
        new DateTime(2026, 9, 1), 1, 1, 0, ["NVL-01"], "ACTIVE", null, null, taxCode, false, shares);

    private static Mock<ITaxRepository> WardRepository()
    {
        var repository = new Mock<ITaxRepository>();
        repository.Setup(x => x.GetPolicyAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(Policy);
        repository.Setup(x => x.ListWardHouseholdsAsync(10, It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Household(1, "Quán A", shares: true, "048090001234"), Household(2, "Quán B", shares: false, null)]);
        repository.Setup(x => x.GetYearRevenueAsync(It.IsAny<IReadOnlyCollection<long>>(), 2026, It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new YearRevenueRow(1, 0, 900_000_000m, new DateOnly(2026, 1, 5)),
                new YearRevenueRow(2, 200_000_000m, 1_000_000_000m, new DateOnly(2026, 2, 1)),
            ]);
        repository.Setup(x => x.ListFiledVendorsAsync(It.IsAny<IReadOnlyCollection<long>>(), 2026, "YEAR", 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlySet<long>)new HashSet<long> { 1 });
        repository.Setup(x => x.ListReferralsAsync(10, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                Referral(1, "PENDING"),
                Referral(2, "REPORTED"),
                Referral(3, "DISMISSED"),
            ]);
        repository.Setup(x => x.GetWardNameAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync("Phường Hải Châu 1");
        return repository;
    }

    private static TaxReferralRow Referral(long id, string status) => new(
        id, 10, 2, "Quán B", "NEW_HOUSEHOLD", "VENDOR", 2, "Hộ mới chưa có MST", Oct18.UtcDateTime, Oct18.UtcDateTime,
        status, null, null, null, null, null);

    [Fact]
    public async Task The_wards_dashboard_counts_households_without_naming_any()
    {
        var dashboard = await new GetWardTaxDashboardQueryHandler(WardContext(), WardRepository().Object, Clock())
            .Handle(new GetWardTaxDashboardQuery(null), CancellationToken.None);

        dashboard.HouseholdCount.Should().Be(2);
        dashboard.ActiveCount.Should().Be(2);
        dashboard.WithTaxCode.Should().Be(1);
        dashboard.WithoutTaxCode.Should().Be(1);
        dashboard.SharingRevenue.Should().Be(1);
        dashboard.Groups.Should().Be(new TaxGroupCountsDto(1, 1, 0, 0, 0)); // A 900 triệu = G1, B 1,2 tỷ = G2
        dashboard.NearThresholdCount.Should().Be(1);
        dashboard.OverThresholdCount.Should().Be(1);
        dashboard.NoticeFiled.Should().Be(1);
        dashboard.NoticeNotFiled.Should().Be(1);
        dashboard.NoticeDueDate.Should().Be(new DateOnly(2027, 1, 31));
        dashboard.PendingReferrals.Should().Be(1);
        dashboard.ReportedReferrals.Should().Be(1);
    }

    [Fact]
    public async Task A_household_that_did_not_agree_has_no_revenue_and_no_group_in_the_wards_list()
    {
        var list = await new ListWardTaxHouseholdsQueryHandler(WardContext(), WardRepository().Object, Clock())
            .Handle(new ListWardTaxHouseholdsQuery(null), CancellationToken.None);

        var shared = list.Households.Single(h => h.DisplayName == "Quán A");
        var hidden = list.Households.Single(h => h.DisplayName == "Quán B");
        shared.Revenue.Should().Be(900_000_000m);
        shared.Group.Should().Be("G1");
        shared.TaxCodeMasked.Should().Be("••••••••1234");
        shared.NoticeFiled.Should().BeTrue();
        hidden.SharesRevenue.Should().BeFalse();
        hidden.Revenue.Should().BeNull("B earned 1,2 tỷ but never agreed to share it");
        hidden.Group.Should().BeNull();
        hidden.HasTaxCode.Should().BeFalse();
        hidden.NoticeFiled.Should().BeFalse();
    }

    [Fact]
    public async Task The_wards_workbook_never_contains_a_revenue_figure_the_household_kept_private()
    {
        WardTaxExport? exported = null;
        var renderer = new Mock<ITaxDocumentRenderer>();
        renderer.Setup(x => x.RenderWardTaxXlsx(It.IsAny<WardTaxExport>()))
            .Callback<WardTaxExport>(export => exported = export)
            .Returns([1]);

        var file = await new ExportWardTaxQueryHandler(WardContext(), WardRepository().Object, renderer.Object, Clock())
            .Handle(new ExportWardTaxQuery(null), CancellationToken.None);

        file.FileName.Should().Be("thue-ho-kinh-doanh-2026.xlsx");
        exported!.WardName.Should().Be("Phường Hải Châu 1");
        exported.Households.Single(h => h.DisplayName == "Quán A").Revenue.Should().Be(900_000_000m);
        exported.Households.Single(h => h.DisplayName == "Quán B").Revenue.Should().BeNull();
        exported.Referrals.Should().HaveCount(3);
        exported.Summary.Should().Contain(line => line.Label.StartsWith("Tổng số hộ") && line.Value == "2");
    }

    [Fact]
    public async Task A_year_with_no_policy_cannot_be_shown_to_the_ward()
    {
        var repository = WardRepository();
        repository.Setup(x => x.GetPolicyAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync((TaxPolicyRow?)null);

        var act = () => new GetWardTaxDashboardQueryHandler(WardContext(), repository.Object, Clock())
            .Handle(new GetWardTaxDashboardQuery(null), CancellationToken.None);

        await act.Should().ThrowAsync<DomainRuleException>();
    }

    // --------------------------------------------------------------------- ward: referrals

    [Fact]
    public async Task Reporting_a_referral_that_is_no_longer_pending_is_a_conflict()
    {
        var repository = WardRepository();
        repository.Setup(x => x.HandleReferralAsync(10, 9, 2, "REPORTED", "CV-12", null, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TaxReferralRow?)null);

        var act = () => new ReportTaxReferralCommandHandler(WardContext(), repository.Object, Clock())
            .Handle(new ReportTaxReferralCommand(9, new ReportTaxReferralRequest("CV-12", " ")), CancellationToken.None);

        await act.Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task A_reported_referral_carries_the_officers_reference()
    {
        var repository = WardRepository();
        repository.Setup(x => x.HandleReferralAsync(10, 1, 2, "REPORTED", "CV-12", "Gửi qua thuế cơ sở", It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Referral(1, "REPORTED") with { AuthorityReference = "CV-12", HandlingNote = "Gửi qua thuế cơ sở", HandledByName = "Cán bộ Vân" });

        var referral = await new ReportTaxReferralCommandHandler(WardContext(), repository.Object, Clock())
            .Handle(new ReportTaxReferralCommand(1, new ReportTaxReferralRequest(" CV-12 ", " Gửi qua thuế cơ sở ")), CancellationToken.None);

        referral.Status.Should().Be("REPORTED");
        referral.ReasonLabel.Should().Be("Hộ mới, chưa có mã số thuế");
        referral.AuthorityReference.Should().Be("CV-12");
    }

    [Fact]
    public void Dismissing_a_referral_needs_a_reason() =>
        new DismissTaxReferralCommandValidator()
            .Validate(new DismissTaxReferralCommand(1, new DismissTaxReferralRequest(" ")))
            .IsValid.Should().BeFalse();

    [Fact]
    public async Task Refreshing_reports_what_was_added_and_what_is_pending()
    {
        var repository = WardRepository();
        repository.Setup(x => x.RefreshReferralsAsync(10, It.IsAny<DateTime>(), TaxReferralWindow.LookbackDays, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TaxReferralRefreshResult(3, 1));
        repository.Setup(x => x.ListReferralsAsync(10, "PENDING", It.IsAny<CancellationToken>())).ReturnsAsync([Referral(1, "PENDING"), Referral(4, "PENDING")]);

        var result = await new RefreshTaxReferralsCommandHandler(WardContext(), repository.Object, Clock())
            .Handle(new RefreshTaxReferralsCommand(), CancellationToken.None);

        result.Should().Be(new TaxReferralRefreshDto(3, 1, 2));
    }

    // ----------------------------------------------------------------- ward: announcements

    [Fact]
    public async Task An_announcement_to_a_group_with_nobody_in_it_is_refused()
    {
        var repository = WardRepository();
        repository.Setup(x => x.ListRecipientUserIdsAsync(10, "NOT_FILED", 2026, It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<long>());

        var act = () => new SendTaxAnnouncementCommandHandler(WardContext(), repository.Object, Clock())
            .Handle(new SendTaxAnnouncementCommand(new SendTaxAnnouncementRequest("NOT_FILED", null, "Nhắc", "Nội dung")), CancellationToken.None);

        await act.Should().ThrowAsync<DomainRuleException>().WithMessage("*Không có hộ nào*");
    }

    [Fact]
    public async Task An_announcement_goes_to_the_households_of_the_chosen_group()
    {
        var repository = WardRepository();
        repository.Setup(x => x.ListRecipientUserIdsAsync(10, "NO_TAX_CODE", 2026, It.IsAny<CancellationToken>())).ReturnsAsync(new long[] { 102, 107 });
        repository.Setup(x => x.CreateAnnouncementAsync(10, 2, "NO_TAX_CODE", 2026, "Khai mã số thuế", "Vào mục Thuế.",
                It.Is<IReadOnlyList<long>>(ids => ids.SequenceEqual(new long[] { 102, 107 })), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TaxAnnouncementRow(1, 10, "NO_TAX_CODE", 2026, "Khai mã số thuế", "Vào mục Thuế.", 2, "Cán bộ Vân", Oct18.UtcDateTime));

        var announcement = await new SendTaxAnnouncementCommandHandler(WardContext(), repository.Object, Clock())
            .Handle(new SendTaxAnnouncementCommand(new SendTaxAnnouncementRequest("NO_TAX_CODE", null, " Khai mã số thuế ", " Vào mục Thuế. ")), CancellationToken.None);

        announcement.RecipientCount.Should().Be(2);
    }

    [Theory]
    [InlineData("ALL", "Tiêu đề", "Nội dung", true)]
    [InlineData("EVERYONE", "Tiêu đề", "Nội dung", false)]
    [InlineData("ALL", "", "Nội dung", false)]
    [InlineData("ALL", "Tiêu đề", "", false)]
    public void An_announcement_needs_a_known_group_a_title_and_a_body(string segment, string title, string body, bool valid) =>
        new SendTaxAnnouncementCommandValidator()
            .Validate(new SendTaxAnnouncementCommand(new SendTaxAnnouncementRequest(segment, null, title, body)))
            .IsValid.Should().Be(valid);

    // ------------------------------------------------------------------------------- admin

    private static CreateTaxPolicyRequest NewPolicy(
        Func<CreateTaxPolicyRequest, CreateTaxPolicyRequest>? change = null)
    {
        var request = new CreateTaxPolicyRequest(
            2027, "Nghị định mới", 1_000_000_000m, 1_000_000_000m, 3_000_000_000m, 50_000_000_000m, true, 0.15m, 0.17m, 0.20m,
            1, 31, 0.8m, null,
            [
                new TaxIndustryRateDto("FOOD_SERVICE", "Ăn uống", 0.03m, 0.015m, null),
                new TaxIndustryRateDto("OTHER", "Khác", 0.02m, 0.01m, null),
            ]);
        return change is null ? request : change(request);
    }

    [Fact]
    public void A_complete_policy_is_valid() =>
        new CreateTaxPolicyCommandValidator().Validate(new CreateTaxPolicyCommand(NewPolicy())).IsValid.Should().BeTrue();

    [Theory]
    [MemberData(nameof(InvalidPolicies))]
    public void An_inconsistent_policy_is_refused(string reason, CreateTaxPolicyRequest request)
    {
        var result = new CreateTaxPolicyCommandValidator().Validate(new CreateTaxPolicyCommand(request));

        result.IsValid.Should().BeFalse(reason);
    }

    public static IEnumerable<object[]> InvalidPolicies()
    {
        yield return ["year before 2026", NewPolicy(p => p with { TaxYear = 2025 })];
        yield return ["no legal basis", NewPolicy(p => p with { LegalBasis = " " })];
        yield return ["ceilings not increasing", NewPolicy(p => p with { Group2Ceiling = 900_000_000m })];
        yield return ["group 3 below group 2", NewPolicy(p => p with { Group3Ceiling = 2_000_000_000m })];
        yield return ["fractional threshold", NewPolicy(p => p with { RevenueThreshold = 1_000_000_000.5m })];
        yield return ["rate above 100%", NewPolicy(p => p with { PitProfitRateG2 = 1.5m })];
        yield return ["warn ratio 1", NewPolicy(p => p with { WarnRatio = 1m })];
        yield return ["30 February", NewPolicy(p => p with { AnnualNoticeMonth = 2, AnnualNoticeDay = 30 })];
        yield return ["no rates", NewPolicy(p => p with { Rates = [] })];
        yield return ["no FOOD_SERVICE", NewPolicy(p => p with { Rates = [new TaxIndustryRateDto("OTHER", "Khác", 0.02m, 0.01m, null)] })];
        yield return ["duplicate code", NewPolicy(p => p with
        {
            Rates = [.. p.Rates, new TaxIndustryRateDto("OTHER", "Khác nữa", 0.02m, 0.01m, null)],
        })];
        yield return ["lower-case code", NewPolicy(p => p with
        {
            Rates = [.. p.Rates, new TaxIndustryRateDto("goods", "Hàng hoá", 0.01m, 0.005m, null)],
        })];
        yield return ["vat rate above 100%", NewPolicy(p => p with
        {
            Rates = [new TaxIndustryRateDto("FOOD_SERVICE", "Ăn uống", 3m, 0.015m, null), new TaxIndustryRateDto("OTHER", "Khác", 0.02m, 0.01m, null)],
        })];
    }

    [Fact]
    public async Task Publishing_a_policy_is_done_by_an_administrator_and_is_the_version_in_force()
    {
        var admin = new Mock<IPlatformAdminContext>();
        admin.Setup(x => x.RequireAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new PlatformAdminActor(1, "Admin"));
        var repository = new Mock<ITaxRepository>();
        NewTaxPolicy? sent = null;
        repository.Setup(x => x.CreatePolicyAsync(1, It.IsAny<NewTaxPolicy>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Callback<long, NewTaxPolicy, DateTime, CancellationToken>((_, policy, _, _) => sent = policy)
            .ReturnsAsync(Policy with { TaxYear = 2027, VersionNo = 1 });

        var created = await new CreateTaxPolicyCommandHandler(admin.Object, repository.Object, Clock())
            .Handle(new CreateTaxPolicyCommand(NewPolicy(p => p with { LegalBasis = "  Nghị định mới  " })), CancellationToken.None);

        created.InForce.Should().BeTrue();
        created.Policy.TaxYear.Should().Be(2027);
        sent!.LegalBasis.Should().Be("Nghị định mới");
        sent.Rates.Should().HaveCount(2);
    }

    [Fact]
    public async Task Only_the_latest_version_of_each_year_is_marked_in_force()
    {
        var admin = new Mock<IPlatformAdminContext>();
        admin.Setup(x => x.RequireAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new PlatformAdminActor(1, "Admin"));
        var repository = new Mock<ITaxRepository>();
        repository.Setup(x => x.ListPoliciesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([Policy with { VersionNo = 2 }, Policy with { PolicyId = 9, VersionNo = 1 }, Policy with { PolicyId = 3, TaxYear = 2027, VersionNo = 1 }]);

        var list = await new ListTaxPoliciesQueryHandler(admin.Object, repository.Object)
            .Handle(new ListTaxPoliciesQuery(), CancellationToken.None);

        list.Select(v => (v.Policy.TaxYear, v.Policy.VersionNo, v.InForce))
            .Should().Equal((2026, 2, true), (2026, 1, false), (2027, 1, true));
    }

    // --------------------------------------------------------------------------------- sweep

    private static Mock<ITaxRepository> SweepRepository(DateOnly today, params (long Vendor, long User)[] vendors)
    {
        var repository = new Mock<ITaxRepository>();
        repository.Setup(x => x.RefreshReferralsAsync(null, It.IsAny<DateTime>(), TaxReferralWindow.LookbackDays, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TaxReferralRefreshResult(2, 1));
        repository.Setup(x => x.ListTaxableVendorsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<(long VendorId, long UserId)>)vendors.Select(v => (v.Vendor, v.User)).ToList());
        repository.Setup(x => x.GetPolicyAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(Policy);
        repository.Setup(x => x.ListFiledVendorsAsync(It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlySet<long>)new HashSet<long>());
        repository.Setup(x => x.GetYearRevenueAsync(It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<int>(), It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<YearRevenueRow>());
        repository.Setup(x => x.NotifyOnceAsync(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<long>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return repository;
    }

    [Fact]
    public async Task The_sweep_warns_households_near_over_or_forecast_to_cross_the_threshold()
    {
        var today = new DateOnly(2026, 10, 18);
        var repository = SweepRepository(today, (1, 101), (2, 102), (3, 103), (4, 104));
        repository.Setup(x => x.GetYearRevenueAsync(It.IsAny<IReadOnlyCollection<long>>(), 2026, today, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new YearRevenueRow(1, 0, 850_000_000m, new DateOnly(2026, 1, 2)), // near
                new YearRevenueRow(2, 0, 1_200_000_000m, new DateOnly(2026, 1, 2)), // over
                new YearRevenueRow(3, 0, 100_000_000m, new DateOnly(2026, 1, 2)), // slow: nothing
                new YearRevenueRow(4, 0, 600_000_000m, new DateOnly(2026, 8, 1)), // under 80%, but 8.6 triệu/day crosses in weeks
            ]);

        var result = await new RunTaxSweepCommandHandler(repository.Object, Clock()).Handle(new RunTaxSweepCommand(), CancellationToken.None);

        result.ReferralsCreated.Should().Be(2);
        result.ReferralsClosed.Should().Be(1);
        result.ThresholdReminders.Should().Be(3);
        repository.Verify(x => x.NotifyOnceAsync(101, "TAX", It.Is<string>(t => t.Contains("gần ngưỡng")), It.IsAny<string>(), "TAX_THRESHOLD", 20261, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(x => x.NotifyOnceAsync(102, "TAX", It.Is<string>(t => t.Contains("đã vượt")), It.IsAny<string>(), "TAX_THRESHOLD", 20262, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(x => x.NotifyOnceAsync(104, "TAX", It.Is<string>(t => t.Contains("dự kiến vượt")), It.IsAny<string>(), "TAX_THRESHOLD", 20263, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(x => x.NotifyOnceAsync(103, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task The_sweep_reminds_only_the_households_that_must_declare_a_quarter_about_to_fall_due()
    {
        // 18 October: Q3 ended on 30 September and is due on 31 October (13 days). Only a household
        // whose revenue to 30 September was above the threshold has to declare it.
        var today = new DateOnly(2026, 10, 18);
        var repository = SweepRepository(today, (1, 101), (2, 102));
        repository.Setup(x => x.GetYearRevenueAsync(It.IsAny<IReadOnlyCollection<long>>(), 2026, new DateOnly(2026, 9, 30), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new YearRevenueRow(1, 0, 400_000_000m, new DateOnly(2026, 1, 2)),
                new YearRevenueRow(2, 0, 1_100_000_000m, new DateOnly(2026, 1, 2)),
            ]);

        var result = await new RunTaxSweepCommandHandler(repository.Object, Clock()).Handle(new RunTaxSweepCommand(), CancellationToken.None);

        result.DeadlineReminders.Should().Be(1);
        repository.Verify(x => x.NotifyOnceAsync(102, "TAX", It.Is<string>(t => t.Contains("Khai thuế quý 3/2026")), It.IsAny<string>(), "TAX_DEADLINE", 2026031, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("2027-01-20", 2026001)] // 11 days left: first reminder
    [InlineData("2027-01-29", 2026002)] // 2 days left: last call
    [InlineData("2027-02-10", 2026003)] // overdue
    public async Task The_sweep_reminds_every_household_that_has_not_filed_the_yearly_notice(string day, long expectedRecord)
    {
        var date = DateOnly.Parse(day);
        var repository = SweepRepository(date, (1, 101), (2, 102));
        repository.Setup(x => x.ListFiledVendorsAsync(It.IsAny<IReadOnlyCollection<long>>(), 2026, "YEAR", 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlySet<long>)new HashSet<long> { 2 }); // vendor 2 already filed

        var result = await new RunTaxSweepCommandHandler(repository.Object, Clock(new DateTimeOffset(date.ToDateTime(new TimeOnly(10, 0)), TimeSpan.FromHours(7))))
            .Handle(new RunTaxSweepCommand(), CancellationToken.None);

        result.DeadlineReminders.Should().Be(1);
        repository.Verify(x => x.NotifyOnceAsync(101, "TAX", It.IsAny<string>(), It.IsAny<string>(), "TAX_DEADLINE", expectedRecord, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(x => x.NotifyOnceAsync(102, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), "TAX_DEADLINE", It.IsAny<long>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_notice_overdue_for_over_a_month_is_left_alone()
    {
        var date = new DateOnly(2027, 3, 15); // 43 days late
        var repository = SweepRepository(date, (1, 101));

        var result = await new RunTaxSweepCommandHandler(repository.Object, Clock(new DateTimeOffset(date.ToDateTime(new TimeOnly(10, 0)), TimeSpan.FromHours(7))))
            .Handle(new RunTaxSweepCommand(), CancellationToken.None);

        result.DeadlineReminders.Should().Be(0);
    }

    [Fact]
    public async Task Running_the_sweep_again_sends_nothing_new()
    {
        var today = new DateOnly(2026, 10, 18);
        var repository = SweepRepository(today, (1, 101));
        repository.Setup(x => x.GetYearRevenueAsync(It.IsAny<IReadOnlyCollection<long>>(), 2026, today, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new YearRevenueRow(1, 0, 850_000_000m, new DateOnly(2026, 1, 2))]);
        repository.Setup(x => x.NotifyOnceAsync(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<long>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false); // the repository found the notification already there

        var result = await new RunTaxSweepCommandHandler(repository.Object, Clock()).Handle(new RunTaxSweepCommand(), CancellationToken.None);

        result.ThresholdReminders.Should().Be(0);
        result.DeadlineReminders.Should().Be(0);
    }

    [Fact]
    public async Task With_no_households_the_sweep_still_refreshes_the_referrals()
    {
        var repository = SweepRepository(new DateOnly(2026, 10, 18));

        var result = await new RunTaxSweepCommandHandler(repository.Object, Clock()).Handle(new RunTaxSweepCommand(), CancellationToken.None);

        result.Should().Be(new TaxSweepResult(2, 1, 0, 0));
    }
}
