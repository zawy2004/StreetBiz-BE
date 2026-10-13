using FluentAssertions;
using Moq;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Features.Finance.WardReports;
using StreetBiz.Application.Features.WardSlots;

namespace StreetBiz.Application.Tests;

/// <summary>AIC-08: the summary sentences are fixed rules over the report rows, so each one is pinned here.</summary>
public sealed class ReportSummaryTests
{
    private static readonly DateOnly From = new(2026, 9, 1);
    private static readonly DateOnly To = new(2026, 9, 30);

    private static CollectionReportRow Totals(
        decimal fee = 0, decimal penalty = 0, decimal pending = 0, decimal overdue = 0, decimal penaltyPending = 0) =>
        new(fee, pending, overdue, penalty, penaltyPending, 0, []);

    private static WardDashboardRow Dashboard(
        int total = 20, int rented = 10, int registrations = 0, int applications = 0) =>
        new(total, rented, registrations, applications, rented, 0, 0, 0);

    private static ReportPeriodFacts Facts(
        int violations = 0, int issued = 0, decimal issuedAmount = 0,
        IReadOnlyList<ViolationTypeCountRow>? top = null,
        int paid = 0, int onTime = 0, int overdueItems = 0, int overdueVendors = 0, OverdueDebtorRow? debtor = null) =>
        new(violations, issued, issuedAmount, top ?? [], paid, onTime, overdueItems, overdueVendors, debtor);

    private static ReportSummaryDto Build(
        CollectionReportRow? current = null, CollectionReportRow? previous = null,
        WardDashboardRow? dashboard = null, ReportPeriodFacts? facts = null) =>
        ReportSummaryBuilder.Build(
            From, To, current ?? Totals(), previous ?? Totals(), dashboard ?? Dashboard(), facts ?? Facts());

    private static ReportSummaryPointDto Single(ReportSummaryDto dto, string topic) =>
        dto.Points.Single(p => p.Topic == topic);

    [Fact]
    public void Collection_that_grew_says_so_with_the_percentage_and_the_split()
    {
        var dto = Build(current: Totals(fee: 3_000_000, penalty: 500_000), previous: Totals(fee: 2_000_000, penalty: 500_000));

        var point = Single(dto, SummaryTopics.Collection);
        point.Tone.Should().Be(SummaryTones.Positive);
        point.Text.Should().Contain("3.500.000 đ").And.Contain("tăng 40%").And.Contain("tiền thuê 3.000.000 đ");
    }

    [Fact]
    public void Collection_that_fell_is_flagged_for_attention()
    {
        var dto = Build(current: Totals(fee: 1_000_000), previous: Totals(fee: 2_000_000));

        var point = Single(dto, SummaryTopics.Collection);
        point.Tone.Should().Be(SummaryTones.Attention);
        point.Text.Should().Contain("giảm 50%");
    }

    [Fact]
    public void A_change_inside_the_five_percent_band_is_called_steady()
    {
        var dto = Build(current: Totals(fee: 2_040_000), previous: Totals(fee: 2_000_000));

        Single(dto, SummaryTopics.Collection).Tone.Should().Be(SummaryTones.Neutral);
        Single(dto, SummaryTopics.Collection).Text.Should().Contain("gần như không đổi");
    }

    [Fact]
    public void Nothing_collected_in_either_period_does_not_divide_by_zero()
    {
        var dto = Build();

        Single(dto, SummaryTopics.Collection).Text.Should().Contain("Chưa ghi nhận khoản thu nào");
    }

    [Fact]
    public void Nothing_collected_this_period_after_a_collecting_one_is_an_attention_point()
    {
        var dto = Build(current: Totals(), previous: Totals(fee: 900_000));

        var point = Single(dto, SummaryTopics.Collection);
        point.Tone.Should().Be(SummaryTones.Attention);
        point.Text.Should().Contain("Chưa thu được khoản nào").And.Contain("900.000 đ");
    }

    [Theory]
    [InlineData(9, 10, SummaryTones.Positive)]
    [InlineData(8, 10, SummaryTones.Neutral)]
    [InlineData(6, 10, SummaryTones.Attention)]
    public void On_time_payment_share_picks_the_tone(int onTime, int paid, string tone)
    {
        var dto = Build(facts: Facts(paid: paid, onTime: onTime));

        Single(dto, SummaryTopics.Timeliness).Tone.Should().Be(tone);
    }

    [Fact]
    public void The_on_time_line_is_left_out_when_nothing_was_paid_in_the_period()
    {
        Build().Points.Should().NotContain(p => p.Topic == SummaryTopics.Timeliness);
    }

    [Fact]
    public void Overdue_debt_names_the_biggest_debtor_and_how_many_households_owe()
    {
        var dto = Build(
            current: Totals(overdue: 2_700_000),
            facts: Facts(overdueItems: 3, overdueVendors: 2, debtor: new OverdueDebtorRow("Phạm Thị Lan", 1_800_000)));

        var point = dto.Points.First(p => p.Topic == SummaryTopics.Debt);
        point.Tone.Should().Be(SummaryTones.Attention);
        point.Text.Should().Contain("3 kỳ phí quá hạn của 2 hộ").And.Contain("2.700.000 đ")
            .And.Contain("Phạm Thị Lan (1.800.000 đ)");
    }

    [Fact]
    public void No_overdue_instalments_is_good_news_and_unpaid_penalties_are_still_mentioned()
    {
        var dto = Build(current: Totals(penaltyPending: 500_000, pending: 900_000));

        var debt = dto.Points.Where(p => p.Topic == SummaryTopics.Debt).ToList();
        debt[0].Tone.Should().Be(SummaryTones.Positive);
        debt.Should().Contain(p => p.Text.Contains("500.000 đ tiền phạt chưa nộp"));
        debt.Should().Contain(p => p.Text.Contains("900.000 đ tiền thuê chưa đến hạn"));
    }

    [Fact]
    public void Violations_list_the_most_common_types_and_the_ones_without_a_standing_penalty()
    {
        var dto = Build(facts: Facts(
            violations: 5, issued: 3, issuedAmount: 1_500_000,
            top: [new ViolationTypeCountRow("OUTSIDE_SLOT", "Bày hàng lấn ô", 3), new ViolationTypeCountRow("NOISE", "Gây ồn", 2)]));

        var point = Single(dto, SummaryTopics.Violations);
        point.Tone.Should().Be(SummaryTones.Attention);
        point.Text.Should().Contain("5 vi phạm").And.Contain("Bày hàng lấn ô (3), Gây ồn (2)")
            .And.Contain("3 vụ (1.500.000 đ)").And.Contain("2 vụ chưa có khoản phạt hiệu lực");
    }

    [Fact]
    public void A_period_without_violations_says_so()
    {
        var point = Single(Build(), SummaryTopics.Violations);

        point.Tone.Should().Be(SummaryTones.Positive);
        point.Text.Should().Contain("Không ghi nhận vi phạm");
    }

    [Theory]
    [InlineData(20, 17, SummaryTones.Positive)]
    [InlineData(20, 10, SummaryTones.Neutral)]
    [InlineData(20, 5, SummaryTones.Attention)]
    public void Occupancy_picks_the_tone(int total, int rented, string tone)
    {
        var dto = Build(dashboard: Dashboard(total, rented));

        Single(dto, SummaryTopics.Occupancy).Tone.Should().Be(tone);
    }

    [Fact]
    public void A_ward_with_no_slots_gets_no_occupancy_line()
    {
        Build(dashboard: Dashboard(total: 0, rented: 0)).Points.Should().NotContain(p => p.Topic == SummaryTopics.Occupancy);
    }

    [Theory]
    [InlineData(0, 0, SummaryTones.Positive)]
    [InlineData(2, 1, SummaryTones.Neutral)]
    [InlineData(4, 3, SummaryTones.Attention)]
    public void Waiting_work_picks_the_tone(int registrations, int applications, string tone)
    {
        Single(Build(dashboard: Dashboard(registrations: registrations, applications: applications)), SummaryTopics.Workload)
            .Tone.Should().Be(tone);
    }

    [Fact]
    public void The_headline_carries_the_period_and_the_three_numbers_an_officer_looks_for_first()
    {
        var dto = Build(
            current: Totals(fee: 3_350_000, overdue: 900_000),
            facts: Facts(violations: 2));

        dto.Headline.Should().Be("01/09/2026 – 30/09/2026: thu 3.350.000 đ, nợ quá hạn 900.000 đ, 2 vi phạm.");
        dto.Basis.Should().Be(ReportSummaryBuilder.Basis);
    }

    [Fact]
    public async Task The_handler_compares_with_the_equally_long_period_just_before()
    {
        var reports = new Mock<IWardReportRepository>();
        reports.Setup(x => x.GetCollectionReportAsync(10, It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Totals());
        reports.Setup(x => x.GetDashboardAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(Dashboard());
        reports.Setup(x => x.GetPeriodFactsAsync(10, It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Facts());

        await new GetReportSummaryQueryHandler(Actor(10), reports.Object, Clock(new DateTime(2026, 9, 23, 3, 0, 0, DateTimeKind.Utc)))
            .Handle(new GetReportSummaryQuery(new DateOnly(2026, 9, 11), new DateOnly(2026, 9, 20)), CancellationToken.None);

        // 10 days: 11..20 September, compared with 1..10 September.
        reports.Verify(x => x.GetCollectionReportAsync(
            10, new DateOnly(2026, 9, 11), new DateOnly(2026, 9, 20), It.IsAny<CancellationToken>()), Times.Once);
        reports.Verify(x => x.GetCollectionReportAsync(
            10, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 10), It.IsAny<CancellationToken>()), Times.Once);
        reports.Verify(x => x.GetPeriodFactsAsync(
            10, new DateOnly(2026, 9, 11), new DateOnly(2026, 9, 20), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task The_handler_defaults_to_the_month_so_far()
    {
        var reports = new Mock<IWardReportRepository>();
        reports.Setup(x => x.GetCollectionReportAsync(11, It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Totals());
        reports.Setup(x => x.GetDashboardAsync(11, It.IsAny<CancellationToken>())).ReturnsAsync(Dashboard());
        reports.Setup(x => x.GetPeriodFactsAsync(11, It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Facts());

        var dto = await new GetReportSummaryQueryHandler(Actor(11), reports.Object, Clock(new DateTime(2026, 9, 23, 3, 0, 0, DateTimeKind.Utc)))
            .Handle(new GetReportSummaryQuery(null, null), CancellationToken.None);

        dto.From.Should().Be(new DateOnly(2026, 9, 1));
        dto.To.Should().Be(new DateOnly(2026, 9, 23));
        reports.Verify(x => x.GetCollectionReportAsync(
            11, new DateOnly(2026, 8, 9), new DateOnly(2026, 8, 31), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void A_period_longer_than_a_year_is_refused()
    {
        var validator = new GetReportSummaryQueryValidator();

        validator.Validate(new GetReportSummaryQuery(new DateOnly(2025, 1, 1), new DateOnly(2026, 6, 1))).IsValid.Should().BeFalse();
        validator.Validate(new GetReportSummaryQuery(new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 1))).IsValid.Should().BeTrue();
    }

    private static TimeProvider Clock(DateTime utcNow)
    {
        var clock = new Mock<TimeProvider>();
        clock.Setup(x => x.GetUtcNow()).Returns(new DateTimeOffset(utcNow, TimeSpan.Zero));
        return clock.Object;
    }

    private static IWardActorContext Actor(int wardId)
    {
        var context = new Mock<IWardActorContext>();
        context.Setup(x => x.RequireAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WardActor(2, wardId, "Cán bộ phường"));
        return context.Object;
    }
}
