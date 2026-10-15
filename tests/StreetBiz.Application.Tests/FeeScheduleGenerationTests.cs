using FluentAssertions;
using Moq;
using StreetBiz.Application.Common.Events;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.Finance;
using StreetBiz.Application.Features.Finance.GenerateFeeSchedule;
using StreetBiz.Application.Features.SidewalkSlots.GetSlotQuote;
using MediatR;

namespace StreetBiz.Application.Tests;

public sealed class FeeScheduleGenerationTests
{
    private static readonly DateOnly Start = new(2026, 9, 1);

    /// <summary>Approved ten days ahead of the start, so no grace period moves a due date.</summary>
    private static readonly DateOnly ApprovedAhead = Start.AddDays(-10);

    /// <summary>The seeded Nguyễn Văn Linh zone: 30 000/day, 3 000/day cleaning, 150 000 admin, 500 000 deposit.</summary>
    private static IReadOnlyList<FeeComponentRow> ZoneOneComponents() =>
    [
        new(1, "Phí vệ sinh môi trường", FeeBases.PerDay, 3_000m, 1),
        new(2, "Phí quản lý hồ sơ", FeeBases.PerTerm, 150_000m, 2),
        new(3, "Tiền đặt cọc", FeeBases.PerTerm, 500_000m, 3),
    ];

    private static FeePlan PlanFor(int termDays, decimal pricePerDay = 30_000m, DateOnly? issuedOn = null) =>
        FeeInstalmentPlanner.Plan(
            FeeQuoteCalculator.Calculate(1, pricePerDay, termDays, ZoneOneComponents()),
            Start,
            issuedOn ?? ApprovedAhead);

    [Fact]
    public void Only_the_rent_is_billed_because_the_quote_shows_other_components_as_reference_only()
    {
        var quote = FeeQuoteCalculator.Calculate(1, 30_000m, 90, ZoneOneComponents());

        var plan = FeeInstalmentPlanner.Plan(quote, Start, ApprovedAhead);

        // "Tiền thuê tính vào hợp đồng" = 90 x 30 000; cleaning, admin fee and deposit are "chưa tính vào hợp đồng".
        quote.IsReferenceOnly.Should().BeTrue();
        plan.Total.Should().Be(quote.BaseFee).And.Be(2_700_000m);
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
    public void Every_full_period_costs_the_same_with_nothing_extra_on_the_first()
    {
        var plan = PlanFor(90);

        plan.Instalments.Select(instalment => instalment.Amount).Should().Equal(900_000m, 900_000m, 900_000m);
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
    public void A_contract_starting_the_day_it_is_approved_gets_a_week_to_pay_its_first_instalment()
    {
        var plan = PlanFor(90, issuedOn: Start);

        plan.Instalments.Select(instalment => instalment.DueDate).Should().Equal(
            Start.AddDays(FeeInstalmentPlanner.GraceDays),
            new DateOnly(2026, 10, 1),
            new DateOnly(2026, 10, 31));
        // The period itself is unchanged: only when it is due moves.
        plan.Instalments[0].PeriodStart.Should().Be(Start);
    }

    [Fact]
    public void A_schedule_generated_late_never_opens_with_instalments_already_overdue()
    {
        // Generated 40 days into the term: periods 1 and 2 have begun, period 3 has not.
        var issued = Start.AddDays(40);

        var plan = PlanFor(90, issuedOn: issued);

        plan.Instalments.Select(instalment => instalment.DueDate).Should().Equal(
            issued.AddDays(7), issued.AddDays(7), new DateOnly(2026, 10, 31));
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
        var plan = PlanFor(31, pricePerDay: 33_333.5m);

        plan.Instalments.Sum(instalment => instalment.Amount).Should().Be(plan.Total);
        plan.Instalments.Should().OnlyContain(instalment => instalment.Amount == Math.Round(instalment.Amount, 0));
    }

    [Fact]
    public void A_renewal_bills_exactly_the_days_it_adds_from_the_day_after_the_old_end()
    {
        // 45 days added after a term ending 29/11, approved well ahead.
        var plan = FeeInstalmentPlanner.PlanExtension(
            30_000m, new DateOnly(2026, 11, 30), new DateOnly(2027, 1, 13), new DateOnly(2026, 11, 1));

        plan.Total.Should().Be(45 * 30_000m);
        plan.Instalments.Select(instalment => (instalment.DueDate, instalment.Amount)).Should().Equal(
            (new DateOnly(2026, 11, 30), 900_000m),
            (new DateOnly(2026, 12, 30), 450_000m));
        plan.Instalments.Last().PeriodEnd.Should().Be(new DateOnly(2027, 1, 13));
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

        await Handler(finance).Handle(new GenerateFeeScheduleCommand(7, 42), CancellationToken.None);

        finance.Verify(repository => repository.ReplaceFeeScheduleAsync(
            7,
            42,
            2_700_000m,
            It.Is<IReadOnlyList<FeeInstalment>>(instalments => instalments.Count == 3
                // "Today" (01/09 in Đà Nẵng) is the start date: the first instalment gets the grace week.
                && instalments[0].DueDate == Start.AddDays(7)),
            It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task Generation_reports_a_missing_contract_as_not_found()
    {
        var finance = new Mock<IFinanceRepository>();
        finance.Setup(repository => repository.GetFeeScheduleContextAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync((FeeScheduleContextRow?)null);

        var act = () => Handler(finance).Handle(new GenerateFeeScheduleCommand(7, 42), CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Generation_refuses_a_zone_with_no_price_instead_of_billing_zero()
    {
        var finance = new Mock<IFinanceRepository>();
        finance.Setup(repository => repository.GetFeeScheduleContextAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Context(termDays: 90) with { PricePerDay = 0m });

        var act = () => Handler(finance).Handle(new GenerateFeeScheduleCommand(7, 42), CancellationToken.None);

        await act.Should().ThrowAsync<DomainRuleException>();
    }

    [Theory]
    [InlineData("EXPIRED")]
    [InlineData("CANCELLED")]
    [InlineData("REVOKED")]
    public async Task A_contract_that_is_no_longer_active_is_never_billed_again(string status)
    {
        var finance = new Mock<IFinanceRepository>();
        finance.Setup(repository => repository.GetFeeScheduleContextAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Context(termDays: 90) with { ContractStatus = status });

        var act = () => Handler(finance).Handle(new GenerateFeeScheduleCommand(7, 42), CancellationToken.None);

        await act.Should().ThrowAsync<DomainRuleException>().WithMessage(FinanceMessages.ContractNotActive);
        finance.Verify(repository => repository.ReplaceFeeScheduleAsync(
            It.IsAny<long>(), It.IsAny<long>(), It.IsAny<decimal>(),
            It.IsAny<IReadOnlyList<FeeInstalment>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_renewal_appends_its_added_days_to_the_open_schedule()
    {
        var finance = new Mock<IFinanceRepository>();
        finance.Setup(repository => repository.GetFeeScheduleContextAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Context(termDays: 90));
        finance.Setup(repository => repository.AppendInstalmentsAsync(
                7, 42, It.IsAny<DateOnly>(), It.IsAny<FeePlan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Schedule());
        var sender = new Mock<ISender>();
        sender.Setup(s => s.Send(It.IsAny<ExtendFeeScheduleCommand>(), It.IsAny<CancellationToken>()))
            .Returns((ExtendFeeScheduleCommand command, CancellationToken token) =>
                new ExtendFeeScheduleCommandHandler(finance.Object, Clock()).Handle(command, token));

        await new ExtendFeeScheduleOnContractRenewed(sender.Object).Handle(
            new RentalContractRenewedEvent(7, Start.AddDays(89), Start.AddDays(119), 42), CancellationToken.None);

        finance.Verify(repository => repository.AppendInstalmentsAsync(
            7,
            42,
            Start.AddDays(90),
            It.Is<FeePlan>(plan => plan.Total == 900_000m && plan.Instalments.Count == 1),
            It.IsAny<CancellationToken>()));
        finance.Verify(repository => repository.ReplaceFeeScheduleAsync(
            It.IsAny<long>(), It.IsAny<long>(), It.IsAny<decimal>(),
            It.IsAny<IReadOnlyList<FeeInstalment>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static GenerateFeeScheduleCommandHandler Handler(Mock<IFinanceRepository> finance) =>
        new(finance.Object, Clock());

    /// <summary>10:00 on the start date in Đà Nẵng.</summary>
    private static TimeProvider Clock() => new FixedClock(new DateTimeOffset(2026, 9, 1, 3, 0, 0, TimeSpan.Zero));

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
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
        TotalAmount: 2_700_000m,
        GeneratedAt: new DateTime(2026, 9, 1, 3, 0, 0, DateTimeKind.Utc),
        SupersededAt: null,
        Items: [new FeeItemRow(21, 11, 1, 1, Start, 2_700_000m, FeeItemStatuses.Pending, null)]);
}
