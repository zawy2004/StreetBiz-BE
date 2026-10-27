using FluentAssertions;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Features.Tax;

namespace StreetBiz.Application.Tests;

/// <summary>The household tax rules: pure functions, so every case is a table of inputs and outputs.</summary>
public sealed class TaxRulesTests
{
    internal static TaxPolicyRow Policy(
        decimal threshold = 1_000_000_000m, bool pitDeductsThreshold = true, int noticeMonth = 1, int noticeDay = 31) => new(
        1, 2026, 2, "Nghị định 141/2026/NĐ-CP", threshold, 1_000_000_000m, 3_000_000_000m, 50_000_000_000m,
        pitDeductsThreshold, 0.15m, 0.17m, 0.20m, noticeMonth, noticeDay, 0.8m, null, new DateTime(2026, 4, 30),
        [
            new TaxIndustryRateRow("FOOD_SERVICE", "Ăn uống", 0.03m, 0.015m, null),
            new TaxIndustryRateRow("GOODS", "Bán hàng hoá", 0.01m, 0.005m, null),
            new TaxIndustryRateRow("OTHER", "Khác", 0.02m, 0.01m, null),
        ]);

    private static TaxIndustryRateRow Food => Policy().Rates[0];

    // ------------------------------------------------------------------------------- groups

    [Theory]
    [InlineData(0, "G1")]
    [InlineData(999_999_999, "G1")]
    [InlineData(1_000_000_000, "G1")] // the threshold itself is still free of tax
    [InlineData(1_000_000_001, "G2")]
    [InlineData(3_000_000_000, "G2")]
    [InlineData(3_000_000_001, "G3")]
    [InlineData(50_000_000_000, "G3")]
    [InlineData(50_000_000_001, "G4")]
    public void Revenue_falls_in_a_group_by_the_policys_boundaries(long revenue, string expected) =>
        TaxGroups.Of(revenue, Policy()).Should().Be(expected);

    [Fact]
    public void The_same_revenue_changes_group_when_the_policy_changes()
    {
        // 600 million: free under Nghị định 141 (1 tỷ), taxable under Nghị định 68 (500 triệu).
        TaxGroups.Of(600_000_000m, Policy(threshold: 1_000_000_000m)).Should().Be("G1");
        TaxGroups.Of(600_000_000m, Policy(threshold: 500_000_000m)).Should().Be("G2");
    }

    [Fact]
    public void A_group_is_described_with_the_policys_own_figures()
    {
        TaxGroups.Describe("G1", Policy()).Should().Be("Doanh thu đến 1 tỷ đồng/năm");
        TaxGroups.Describe("G2", Policy()).Should().Be("Doanh thu trên 1 tỷ đồng đến 3 tỷ đồng/năm");
        TaxGroups.Describe("G1", Policy(threshold: 500_000_000m)).Should().Be("Doanh thu đến 500 triệu đồng/năm");
    }

    [Theory]
    [InlineData(0, "SAFE")]
    [InlineData(799_999_999, "SAFE")]
    [InlineData(800_000_000, "NEAR")] // 80% of 1 tỷ
    [InlineData(1_000_000_000, "NEAR")]
    [InlineData(1_000_000_001, "OVER")]
    public void A_household_is_warned_from_the_warning_share_of_the_threshold(long revenue, string expected) =>
        ThresholdLevels.Of(revenue, Policy()).Should().Be(expected);

    // ---------------------------------------------------------------------------- projection

    private static readonly DateOnly Jan1 = new(2026, 1, 1);

    [Fact]
    public void A_household_is_not_forecast_before_it_has_two_weeks_of_sales()
    {
        var projection = RevenueProjections.Project(
            10_000_000m, new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 11), 2026, 1_000_000_000m);

        projection.Enough.Should().BeFalse();
        projection.DaysOfData.Should().Be(10);
        projection.ProjectedRevenue.Should().Be(10_000_000m);
        projection.CrossingDate.Should().BeNull();
    }

    [Fact]
    public void A_steady_seller_is_carried_to_the_end_of_the_year_and_does_not_cross()
    {
        // 100 days, 100 million: a million a day, 264 days left.
        var projection = RevenueProjections.Project(100_000_000m, Jan1, new DateOnly(2026, 4, 11), 2026, 1_000_000_000m);

        projection.Enough.Should().BeTrue();
        projection.DaysOfData.Should().Be(100);
        projection.DailyRate.Should().Be(1_000_000m);
        projection.ProjectedRevenue.Should().Be(364_000_000m);
        projection.WillCross.Should().BeFalse();
        projection.CrossingDate.Should().BeNull();
    }

    [Fact]
    public void A_fast_seller_gets_the_date_it_is_forecast_to_cross_the_threshold()
    {
        // 6 million a day, 400 million still to go: 67 days after 11 April is 17 June.
        var projection = RevenueProjections.Project(600_000_000m, Jan1, new DateOnly(2026, 4, 11), 2026, 1_000_000_000m);

        projection.WillCross.Should().BeTrue();
        projection.CrossingDate.Should().Be(new DateOnly(2026, 6, 17));
        projection.ProjectedRevenue.Should().Be(2_184_000_000m);
    }

    [Fact]
    public void A_household_already_over_the_threshold_has_no_crossing_date()
    {
        var projection = RevenueProjections.Project(1_200_000_000m, Jan1, new DateOnly(2026, 10, 18), 2026, 1_000_000_000m);

        projection.WillCross.Should().BeTrue();
        projection.CrossingDate.Should().BeNull();
    }

    [Fact]
    public void The_average_counts_from_the_first_day_of_sales_not_from_january()
    {
        // Started 1 July: 30 days, 30 million, so a million a day with 153 days left.
        var projection = RevenueProjections.Project(
            30_000_000m, new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31), 2026, 1_000_000_000m);

        projection.DaysOfData.Should().Be(30);
        projection.ProjectedRevenue.Should().Be(183_000_000m);
    }

    [Fact]
    public void A_finished_year_is_not_forecast_it_is_the_total()
    {
        var projection = RevenueProjections.Project(
            1_300_000_000m, Jan1, new DateOnly(2027, 2, 1), 2026, 1_000_000_000m);

        projection.Enough.Should().BeTrue();
        projection.ProjectedRevenue.Should().Be(1_300_000_000m);
        projection.WillCross.Should().BeTrue();
        projection.CrossingDate.Should().BeNull();
    }

    [Fact]
    public void A_crossing_after_new_year_is_not_a_crossing_this_year()
    {
        // 1 million a day from 1 January and only 100 days gone: it reaches 1 tỷ after the year ends.
        RevenueProjections.Project(100_000_000m, Jan1, new DateOnly(2026, 4, 11), 2026, 1_000_000_000m)
            .CrossingDate.Should().BeNull();
    }

    // ------------------------------------------------------------------------------ estimate

    [Fact]
    public void A_household_at_or_below_the_threshold_owes_nothing()
    {
        var estimate = TaxEstimator.Estimate(1_000_000_000m, Policy(), Food, 0.5m);

        estimate.Taxable.Should().BeFalse();
        estimate.Vat.Should().Be(0);
        estimate.PitOnRevenue.Should().Be(0);
        estimate.TotalOnRevenue.Should().Be(0);
        estimate.PitOnProfit.Should().BeNull("the profit method only matters above the threshold");
    }

    [Fact]
    public void VAT_is_charged_on_all_revenue_and_PIT_on_the_part_above_the_threshold()
    {
        // 2 tỷ, food: VAT 3% of 2 tỷ = 60 triệu; PIT 1.5% of (2 tỷ - 1 tỷ) = 15 triệu.
        var estimate = TaxEstimator.Estimate(2_000_000_000m, Policy(), Food, null);

        estimate.Group.Should().Be("G2");
        estimate.Vat.Should().Be(60_000_000m);
        estimate.PitBase.Should().Be(1_000_000_000m);
        estimate.PitOnRevenue.Should().Be(15_000_000m);
        estimate.TotalOnRevenue.Should().Be(75_000_000m);
        estimate.EffectiveRate.Should().Be(0.0375m);
    }

    [Fact]
    public void When_the_policy_does_not_deduct_the_threshold_PIT_is_on_all_revenue()
    {
        var estimate = TaxEstimator.Estimate(2_000_000_000m, Policy(pitDeductsThreshold: false), Food, null);

        estimate.PitBase.Should().Be(2_000_000_000m);
        estimate.PitOnRevenue.Should().Be(30_000_000m);
    }

    [Theory]
    [InlineData(0.80, 120_000_000, "REVENUE")] // 400 triệu profit x 15% = 60 + VAT 60
    [InlineData(0.98, 66_000_000, "PROFIT")] // 40 triệu profit x 15% = 6 + VAT 60
    public void The_profit_method_is_compared_with_the_revenue_method(double costRatio, long expectedTotal, string cheaper)
    {
        var estimate = TaxEstimator.Estimate(2_000_000_000m, Policy(), Food, (decimal)costRatio);

        estimate.PitProfitRate.Should().Be(0.15m);
        estimate.TotalOnProfit.Should().Be(expectedTotal);
        estimate.Cheaper.Should().Be(cheaper);
    }

    [Fact]
    public void Group_3_uses_its_own_profit_rate_and_costs_are_capped_below_100_percent()
    {
        var estimate = TaxEstimator.Estimate(5_000_000_000m, Policy(), Food, 1.5m);

        estimate.Group.Should().Be("G3");
        estimate.CostRatio.Should().Be(0.99m);
        estimate.PitProfitRate.Should().Be(0.17m);
        estimate.Profit.Should().Be(50_000_000m);
    }

    // ------------------------------------------------------------------------------ calendar

    [Fact]
    public void The_yearly_notice_is_due_on_the_policys_day_of_the_next_year()
    {
        var notice = TaxCalendar.YearNotice(2026, Policy());

        notice.Due.Should().Be(new DateOnly(2027, 1, 31));
        notice.Start.Should().Be(new DateOnly(2026, 1, 1));
        notice.End.Should().Be(new DateOnly(2026, 12, 31));
    }

    [Fact]
    public void A_notice_day_that_does_not_exist_in_its_month_is_clamped()
    {
        TaxCalendar.YearNotice(2026, Policy(noticeMonth: 2, noticeDay: 30)).Due.Should().Be(new DateOnly(2027, 2, 28));
    }

    [Theory]
    [InlineData(1, "2026-03-31", "2026-04-30")]
    [InlineData(2, "2026-06-30", "2026-07-31")]
    [InlineData(3, "2026-09-30", "2026-10-31")]
    [InlineData(4, "2026-12-31", "2027-01-31")]
    public void A_quarter_is_declared_by_the_end_of_the_month_after_it(int quarter, string end, string due)
    {
        var period = TaxCalendar.Quarter(2026, quarter);

        period.End.Should().Be(DateOnly.Parse(end));
        period.Due.Should().Be(DateOnly.Parse(due));
    }

    [Fact]
    public void A_tax_year_has_a_notice_and_four_quarters()
    {
        TaxCalendar.Periods(2026, Policy()).Select(p => (p.Type, p.No))
            .Should().Equal(("YEAR", 0), ("QUARTER", 1), ("QUARTER", 2), ("QUARTER", 3), ("QUARTER", 4));
        TaxCalendar.Find(2026, "QUARTER", 5, Policy()).Should().BeNull();
        TaxCalendar.Find(2026, "YEAR", 1, Policy()).Should().BeNull();
    }

    [Theory]
    [InlineData("2026-12-01", false, true, "NOT_YET")] // the year is still running
    [InlineData("2027-01-05", false, true, "OPEN")] // over, due in 26 days
    [InlineData("2027-01-20", false, true, "DUE_SOON")] // due in 11 days
    [InlineData("2027-02-01", false, true, "OVERDUE")]
    [InlineData("2027-02-01", true, true, "FILED")]
    [InlineData("2027-01-20", false, false, "NOT_REQUIRED")]
    public void A_period_has_a_status_from_the_calendar_and_whether_it_was_filed(
        string today, bool filed, bool required, string expected)
    {
        var period = TaxCalendar.YearNotice(2026, Policy());

        TaxCalendar.Status(period, DateOnly.Parse(today), filed, required).Should().Be(expected);
    }

    [Fact]
    public void A_period_can_only_be_filed_after_it_has_ended()
    {
        var period = TaxCalendar.Quarter(2026, 3);

        TaxCalendar.CanFile(period, new DateOnly(2026, 9, 30)).Should().BeFalse();
        TaxCalendar.CanFile(period, new DateOnly(2026, 10, 1)).Should().BeTrue();
    }

    // ---------------------------------------------------------------------------- obligations

    [Fact]
    public void Group_1_owes_no_tax_but_still_keeps_a_book_and_notifies_its_revenue()
    {
        var codes = TaxObligations.For("G1", Policy(), hasTaxCode: false, eInvoiceRegistered: false);

        codes.Select(o => o.Code).Should().Equal("NO_VAT_PIT", "REGISTER_TAX_CODE", "KEEP_REVENUE_BOOK", "ANNUAL_REVENUE_NOTICE");
        codes.Single(o => o.Code == "REGISTER_TAX_CODE").Done.Should().BeFalse();
        codes.Should().NotContain(o => o.Code == "EINVOICE");
    }

    [Fact]
    public void Above_the_threshold_a_household_must_declare_and_issue_e_invoices()
    {
        var done = TaxObligations.For("G2", Policy(), hasTaxCode: true, eInvoiceRegistered: true);
        var notDone = TaxObligations.For("G2", Policy(), hasTaxCode: true, eInvoiceRegistered: false);

        done.Select(o => o.Code).Should().Contain(["DECLARE_PERIODIC", "EINVOICE", "PAY_TAX"]).And.NotContain("NO_VAT_PIT");
        done.Single(o => o.Code == "EINVOICE").Done.Should().BeTrue();
        notDone.Single(o => o.Code == "EINVOICE").Done.Should().BeFalse();
        done.Single(o => o.Code == "REGISTER_TAX_CODE").Done.Should().BeTrue();
    }

    [Fact]
    public void Groups_3_and_4_have_their_own_extra_duties()
    {
        TaxObligations.For("G3", Policy(), true, true).Should().Contain(o => o.Code == "PROFIT_METHOD");
        TaxObligations.For("G4", Policy(), true, true).Single(o => o.Code == "DECLARE_PERIODIC").Title
            .Should().Contain("theo tháng");
    }

    // ------------------------------------------------------------------------------ household

    private static WardHouseholdFacts Facts(int total, int active, int suspended, string? permit = null) => new(
        1, 5, "Quán A", "Chủ hộ", null, null, null, "FIXED_STOREFRONT", DateTime.UtcNow,
        total, active, suspended, [], permit, null, null, null, false, false);

    [Theory]
    [InlineData(0, 0, 0, null, "NOT_STARTED")]
    [InlineData(2, 1, 0, "ACTIVE", "ACTIVE")]
    [InlineData(2, 1, 0, "SUSPENDED", "SUSPENDED")]
    [InlineData(2, 0, 1, null, "SUSPENDED")]
    [InlineData(2, 0, 0, null, "ENDED")]
    public void A_households_status_comes_from_its_contracts_and_permit(
        int total, int active, int suspended, string? permit, string expected) =>
        HouseholdStatuses.Of(Facts(total, active, suspended, permit)).Should().Be(expected);

    // ------------------------------------------------------------------------------- helpers

    [Theory]
    [InlineData("0123456789", true)]
    [InlineData("048090001234", true)]
    [InlineData("0123456789-001", true)]
    [InlineData("0123456789012", true)]
    [InlineData("12345", false)]
    [InlineData("0123456789-00", false)]
    [InlineData("01234567ab", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_tax_code_has_one_of_the_four_shapes(string? code, bool valid) =>
        TaxCodes.IsValid(code).Should().Be(valid);

    [Fact]
    public void A_tax_code_is_masked_to_its_last_four_digits()
    {
        TaxCodes.Mask("048090001234").Should().Be("••••••••1234");
        TaxCodes.Mask(null).Should().BeEmpty();
        TaxCodes.Mask("123").Should().BeEmpty();
    }

    [Theory]
    [InlineData(1_000_000_000, "1 tỷ đồng")]
    [InlineData(2_500_000_000, "2,5 tỷ đồng")]
    [InlineData(500_000_000, "500 triệu đồng")]
    [InlineData(850_000, "850.000 đồng")]
    public void Money_is_written_the_way_it_is_said(long value, string expected) =>
        TaxFormat.Money(value).Should().Be(expected);
}
