using FluentAssertions;
using Moq;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Features.Finance.WardReports;
using StreetBiz.Application.Features.WardSlots;

namespace StreetBiz.Application.Tests;

/// <summary>WARD-14 report handlers: the period each one asks the repository for, scoped to the officer's ward.</summary>
public sealed class WardCollectionQueryTests
{
    private const int WardId = 10;
    // 2 October 2026, 11:00 in Đà Nẵng.
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 4, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 2);

    private readonly Mock<IWardCollectionRepository> repo = new();
    private readonly Mock<IWardActorContext> actor = new();

    public WardCollectionQueryTests()
    {
        actor.Setup(x => x.RequireAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new WardActor(2, WardId, "Cán bộ"));
        repo.Setup(x => x.ListUnpaidFeeItemsAsync(WardId, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        repo.Setup(x => x.ListZonesAsync(WardId, Today, It.IsAny<CancellationToken>())).ReturnsAsync([new WardZoneRow(1, "Khu 1", 4, 2)]);
        repo.Setup(x => x.ListRefundsAsync(WardId, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        repo.Setup(x => x.ListPenaltyPaymentsAsync(WardId, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static TimeProvider Clock() => new FixedClock(Now);

    private static WardFeeItemRow Fee(DateOnly due, string status, DateTime? paidAt, decimal amount = 1_000_000m) =>
        new(1, due, amount, status, paidAt, IsCurrent: true);

    private static WardRefundRow Refund(long id, string status, DateTime? refundedAt = null) =>
        new(id, "FEE", 500, "Hộ 1", "0905000101", "S-01", "Phí thuê ô kỳ 2", "PAYOS", $"PAYOS-{id}", 1_040_000m,
            new DateTime(2026, 9, 20, 2, 0, 0, DateTimeKind.Utc), status, refundedAt);

    [Fact]
    public async Task Performance_defaults_to_the_first_of_this_month_through_today_in_vietnam()
    {
        var first = new DateOnly(2026, 10, 1);
        repo.Setup(x => x.ListFeeActivityAsync(WardId, first, Today,
                BusinessCalendar.StartOfDayUtc(first), BusinessCalendar.StartOfDayUtc(Today.AddDays(1)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([Fee(first, "PAID", new DateTime(2026, 9, 30, 3, 0, 0, DateTimeKind.Utc))]);

        var result = await new GetCollectionPerformanceQueryHandler(actor.Object, repo.Object, Clock())
            .Handle(new GetCollectionPerformanceQuery(null, null), default);

        (result.From, result.To).Should().Be((first, Today));
        (result.DueCount, result.PaidOnTimeCount, result.OnTimeRate).Should().Be((1, 1, (double?)1.0));
        result.ByZone.Should().ContainSingle().Which.ZoneName.Should().Be("Khu 1");
    }

    [Fact]
    public async Task Performance_uses_the_period_asked_for()
    {
        var from = new DateOnly(2026, 8, 1);
        var to = new DateOnly(2026, 8, 31);
        repo.Setup(x => x.ListFeeActivityAsync(WardId, from, to, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var result = await new GetCollectionPerformanceQueryHandler(actor.Object, repo.Object, Clock())
            .Handle(new GetCollectionPerformanceQuery(from, to), default);

        (result.From, result.To, result.DueCount, result.OnTimeRate).Should().Be((from, to, 0, (double?)null));
        repo.Verify(x => x.ListFeeActivityAsync(WardId, from, to,
            BusinessCalendar.StartOfDayUtc(from), BusinessCalendar.StartOfDayUtc(to.AddDays(1)), It.IsAny<CancellationToken>()));
    }

    [Theory]
    [InlineData(2026, 9, 1, 2026, 8, 31, false)]
    [InlineData(2026, 9, 1, 2026, 9, 1, true)]
    public void Performance_refuses_a_period_that_ends_before_it_starts(int fy, int fm, int fd, int ty, int tm, int td, bool valid) =>
        new GetCollectionPerformanceQueryValidator()
            .Validate(new GetCollectionPerformanceQuery(new DateOnly(fy, fm, fd), new DateOnly(ty, tm, td)))
            .IsValid.Should().Be(valid);

    [Fact]
    public async Task Trend_covers_whole_months_ending_with_this_one_and_adds_penalties()
    {
        // Three months: August, September and October 2026, 1 August to 31 October (Vietnam days).
        var first = new DateOnly(2026, 8, 1);
        var last = new DateOnly(2026, 10, 31);
        repo.Setup(x => x.ListFeeActivityAsync(WardId, first, last,
                BusinessCalendar.StartOfDayUtc(first), BusinessCalendar.StartOfDayUtc(last.AddDays(1)), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Fee(new DateOnly(2026, 9, 5), "PAID", new DateTime(2026, 9, 4, 3, 0, 0, DateTimeKind.Utc))]);
        repo.Setup(x => x.ListPenaltyPaymentsAsync(WardId, BusinessCalendar.StartOfDayUtc(first),
                BusinessCalendar.StartOfDayUtc(last.AddDays(1)), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new WardPenaltyPaymentRow(500_000m, new DateTime(2026, 8, 10, 3, 0, 0, DateTimeKind.Utc))]);

        var trend = await new GetCollectionTrendQueryHandler(actor.Object, repo.Object, Clock())
            .Handle(new GetCollectionTrendQuery(3), default);

        trend.Select(m => (m.Month, m.FeeCollected, m.PenaltyCollected)).Should().Equal(
            (8, 0m, 500_000m), (9, 1_000_000m, 0m), (10, 0m, 0m));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(12, true)]
    [InlineData(13, false)]
    public void Trend_is_one_to_twelve_months(int months, bool valid) =>
        new GetCollectionTrendQueryValidator().Validate(new GetCollectionTrendQuery(months)).IsValid.Should().Be(valid);

    [Fact]
    public async Task Refunds_list_is_the_wards_own_mapped_one_to_one()
    {
        repo.Setup(x => x.ListRefundsAsync(WardId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Refund(7, "REFUND_REQUIRED"), Refund(8, "REFUNDED", new DateTime(2026, 9, 25, 2, 0, 0, DateTimeKind.Utc))]);

        var refunds = await new ListWardRefundsQueryHandler(actor.Object, repo.Object).Handle(new ListWardRefundsQuery(), default);

        refunds.Select(r => (r.TransactionId, r.Status)).Should().Equal((7L, "REFUND_REQUIRED"), (8L, "REFUNDED"));
        refunds[0].Should().BeEquivalentTo(new
        {
            VendorName = "Hộ 1", SlotCode = "S-01", Provider = "PAYOS", ProviderReference = "PAYOS-7", Amount = 1_040_000m,
        });
    }

    [Fact]
    public async Task Marking_a_refund_records_the_officer_the_time_and_a_trimmed_note()
    {
        repo.Setup(x => x.MarkRefundedAsync(WardId, 7, 2, "Đã hoàn qua payOS", Now.UtcDateTime, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Refund(7, "REFUNDED", Now.UtcDateTime));

        var result = await new MarkPaymentRefundedCommandHandler(actor.Object, repo.Object, Clock())
            .Handle(new MarkPaymentRefundedCommand(7, "  Đã hoàn qua payOS  "), default);

        (result.Status, result.RefundedAt).Should().Be(("REFUNDED", (DateTime?)Now.UtcDateTime));
    }

    [Fact]
    public async Task Marking_a_refund_with_a_blank_note_stores_no_note()
    {
        repo.Setup(x => x.MarkRefundedAsync(WardId, 7, 2, null, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Refund(7, "REFUNDED", Now.UtcDateTime));

        await new MarkPaymentRefundedCommandHandler(actor.Object, repo.Object, Clock())
            .Handle(new MarkPaymentRefundedCommand(7, "   "), default);

        repo.Verify(x => x.MarkRefundedAsync(WardId, 7, 2, null, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task Marking_a_refund_that_is_not_the_wards_is_not_found()
    {
        repo.Setup(x => x.MarkRefundedAsync(WardId, 99, 2, It.IsAny<string?>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((WardRefundRow?)null);

        var act = () => new MarkPaymentRefundedCommandHandler(actor.Object, repo.Object, Clock())
            .Handle(new MarkPaymentRefundedCommand(99, null), default);

        await act.Should().ThrowAsync<NotFoundException>().WithMessage(WardCollectionMessages.RefundNotFound);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void A_refund_mark_needs_a_transaction(long id, bool valid) =>
        new MarkPaymentRefundedCommandValidator().Validate(new MarkPaymentRefundedCommand(id, null)).IsValid.Should().Be(valid);

    [Fact]
    public void A_refund_note_is_at_most_500_characters() =>
        new MarkPaymentRefundedCommandValidator().Validate(new MarkPaymentRefundedCommand(1, new string('x', 501)))
            .IsValid.Should().BeFalse();

    [Fact]
    public async Task Debtors_and_receivables_are_read_for_the_officers_ward_only()
    {
        repo.Setup(x => x.GetLastDebtRemindersAsync(It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<long, DateTime>());

        await new ListWardDebtorsQueryHandler(actor.Object, repo.Object, Clock()).Handle(new ListWardDebtorsQuery(), default);
        await new GetReceivablesQueryHandler(actor.Object, repo.Object, Clock()).Handle(new GetReceivablesQuery(), default);

        repo.Verify(x => x.ListUnpaidFeeItemsAsync(WardId, It.IsAny<CancellationToken>()), Times.Exactly(2));
        repo.Verify(x => x.ListUnpaidFeeItemsAsync(It.Is<int>(w => w != WardId), It.IsAny<CancellationToken>()), Times.Never);
        repo.Verify(x => x.ListRefundsAsync(WardId, It.IsAny<CancellationToken>()), Times.Once);
    }
}
