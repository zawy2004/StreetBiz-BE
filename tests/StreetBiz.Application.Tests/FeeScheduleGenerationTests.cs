using FluentAssertions;
using Moq;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.Finance;
using StreetBiz.Application.Features.Finance.GenerateFeeSchedule;
using StreetBiz.Application.Features.SidewalkSlots.GetSlotQuote;

namespace StreetBiz.Application.Tests;

public sealed class FeeScheduleGenerationTests
{
    private static readonly DateOnly Start = new(2026, 9, 1);

    /// <summary>The seeded Nguyễn Văn Linh zone: 30 000/day, 3 000/day cleaning, 150 000 admin, 500 000 deposit.</summary>
    private static IReadOnlyList<FeeComponentRow> ZoneOneComponents() =>
    [
        new(1, "Phí vệ sinh môi trường", FeeBases.PerDay, 3_000m, 1),
        new(2, "Phí quản lý hồ sơ", FeeBases.PerTerm, 150_000m, 2),
        new(3, "Tiền đặt cọc", FeeBases.PerTerm, 500_000m, 3),
    ];

    private static FeePlan PlanFor(int termDays, decimal pricePerDay = 30_000m) =>
        FeeInstalmentPlanner.Plan(
            FeeQuoteCalculator.Calculate(1, pricePerDay, termDays, ZoneOneComponents()),
            Start);

    [Fact]
    public void Plan_total_is_the_quote_total_so_a_contract_never_costs_more_than_quoted()
    {
        var quote = FeeQuoteCalculator.Calculate(1, 30_000m, 90, ZoneOneComponents());

        var plan = FeeInstalmentPlanner.Plan(quote, Start);

        // 90 x (30 000 + 3 000) + 150 000 + 500 000
        quote.Total.Should().Be(3_620_000m);
        plan.Total.Should().Be(quote.Total);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(29)]
    [InlineData(30)]
    [InlineData(31)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(365)]
    public void Instalments_always_sum_to_the_total(int termDays)
    {
        var plan = PlanFor(termDays);

        plan.Instalments.Sum(instalment => instalment.Amount).Should().Be(plan.Total);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(30, 1)]
    [InlineData(31, 2)]
    [InlineData(60, 2)]
    [InlineData(90, 3)]
    [InlineData(180, 6)]
    public void One_instalment_per_thirty_day_period_with_the_remainder_in_the_last(
        int termDays, int expectedCount)
    {
        PlanFor(termDays).Instalments.Should().HaveCount(expectedCount);
    }

    [Fact]
    public void Per_term_components_are_charged_once_on_the_first_instalment()
    {
        var plan = PlanFor(90);

        // 30 x 33 000 = 990 000 recurring, plus 650 000 charged once.
        plan.Instalments[0].Amount.Should().Be(1_640_000m);
        plan.Instalments[1].Amount.Should().Be(990_000m);
        plan.Instalments[2].Amount.Should().Be(990_000m);
    }

    [Fact]
    public void Due_dates_run_every_thirty_days_from_the_contract_start()
    {
        var plan = PlanFor(90);

        plan.Instalments.Select(instalment => instalment.DueDate).Should().Equal(
            new DateOnly(2026, 9, 1),
            new DateOnly(2026, 10, 1),
            new DateOnly(2026, 10, 31));
    }

    [Fact]
    public void Periods_cover_the_term_without_gaps_or_overlap()
    {
        var plan = PlanFor(90);

        plan.Instalments[0].PeriodEnd.Should().Be(new DateOnly(2026, 9, 30));
        plan.Instalments[1].PeriodStart.Should().Be(new DateOnly(2026, 10, 1));
        plan.Instalments.Last().PeriodEnd.Should().Be(Start.AddDays(89));
    }

    [Fact]
    public void A_short_term_is_one_instalment_due_on_the_start_date()
    {
        var plan = PlanFor(7);

        plan.Instalments.Should().ContainSingle();
        plan.Instalments[0].DueDate.Should().Be(Start);
        plan.Instalments[0].Amount.Should().Be(plan.Total);
    }

    [Fact]
    public void Rounding_leftovers_land_on_the_last_instalment_not_on_the_total()
    {
        // A price that does not divide evenly across the periods.
        var plan = PlanFor(31, pricePerDay: 33_333m);

        plan.Instalments.Sum(instalment => instalment.Amount).Should().Be(plan.Total);
        plan.Instalments.Should().OnlyContain(instalment => instalment.Amount == Math.Round(instalment.Amount, 0));
    }

    [Fact]
    public async Task Generation_prices_the_applied_for_term_and_stores_the_matching_total()
    {
        var finance = new Mock<IFinanceRepository>();
        finance.Setup(repository => repository.GetFeeScheduleContextAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Context(termDays: 90));
        finance.Setup(repository => repository.ReplaceFeeScheduleAsync(
                7, 42, It.IsAny<decimal>(), It.IsAny<IReadOnlyList<FeeInstalment>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Schedule());

        await new GenerateFeeScheduleCommandHandler(finance.Object)
            .Handle(new GenerateFeeScheduleCommand(7, 42), CancellationToken.None);

        finance.Verify(repository => repository.ReplaceFeeScheduleAsync(
            7,
            42,
            3_620_000m,
            It.Is<IReadOnlyList<FeeInstalment>>(instalments => instalments.Count == 3),
            It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task Generation_reports_a_missing_contract_as_not_found()
    {
        var finance = new Mock<IFinanceRepository>();
        finance.Setup(repository => repository.GetFeeScheduleContextAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync((FeeScheduleContextRow?)null);

        var act = () => new GenerateFeeScheduleCommandHandler(finance.Object)
            .Handle(new GenerateFeeScheduleCommand(7, 42), CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Generation_refuses_a_zone_with_no_price_instead_of_billing_zero()
    {
        var finance = new Mock<IFinanceRepository>();
        finance.Setup(repository => repository.GetFeeScheduleContextAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Context(termDays: 90) with { PricePerDay = 0m });

        var act = () => new GenerateFeeScheduleCommandHandler(finance.Object)
            .Handle(new GenerateFeeScheduleCommand(7, 42), CancellationToken.None);

        await act.Should().ThrowAsync<DomainRuleException>();
    }

    private static FeeScheduleContextRow Context(int termDays) => new(
        ContractId: 7,
        SlotId: 1,
        SlotCode: "NVL-01",
        ZoneId: 1,
        ZoneName: "Đường Nguyễn Văn Linh",
        VendorId: 1,
        VendorUserId: 5,
        StartDate: Start,
        EndDate: Start.AddDays(termDays),
        ContractStatus: "ACTIVE",
        TermDays: termDays,
        PricePerDay: 30_000m,
        Components: ZoneOneComponents());

    private static FeeScheduleRow Schedule() => new(
        FeeScheduleId: 11,
        ContractId: 7,
        Revision: 1,
        TotalAmount: 3_620_000m,
        GeneratedAt: new DateTime(2026, 9, 1, 3, 0, 0, DateTimeKind.Utc),
        SupersededAt: null,
        Items: [new FeeItemRow(21, 11, 1, 1, Start, 3_620_000m, FeeItemStatuses.Pending, null)]);
}
