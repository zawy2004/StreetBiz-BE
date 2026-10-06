using FluentAssertions;
using Moq;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.Commerce.OrderTracking;
using StreetBiz.Application.Features.Finance.InvoiceDocuments;
using StreetBiz.Application.Features.Finance.VendorContracts;
using StreetBiz.Application.Features.Finance.WardReports;
using StreetBiz.Application.Features.WardSlots;

namespace StreetBiz.Application.Tests;

/// <summary>Vendor fee schedules, ward collections and arrival notices.</summary>
public sealed class FinanceInsightsTests
{
    // 2 October 2026, 11:00 in Đà Nẵng.
    private static readonly DateTime Now = new(2026, 10, 2, 4, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = new(2026, 10, 2);

    // ---- Vendor: a contract's fee schedule -------------------------------------------------

    private static ScheduleItemRow Item(long id, int ordinal, DateOnly due, string status, DateTime? paidAt = null, long contract = 3) =>
        new(id, contract, ordinal, 3, due, 1_040_000m, status, paidAt,
            status == FeeItemStatuses.Paid ? id + 100 : null,
            status == FeeItemStatuses.Paid ? $"HD-2026-{id:000000}" : null);

    private static readonly VendorContractRow Contract = new(
        3, "NVL-01", "Nguyễn Văn Linh", "Phường Hải Châu 1", "45 Nguyễn Văn Linh",
        new DateOnly(2026, 8, 1), new DateOnly(2026, 10, 30), ContractStatuses.Active, 3_120_000m);

    [Fact]
    public void An_unpaid_instalment_past_its_due_date_reads_as_overdue_even_before_the_sweep_runs()
    {
        var dto = FeeScheduleProgress.ToDto(Item(2, 2, Today.AddDays(-5), FeeItemStatuses.Pending), Today);

        dto.ItemStatus.Should().Be(FeeItemStatuses.Overdue);
        dto.DaysOverdue.Should().Be(5);
        dto.DaysUntilDue.Should().BeNull();
        dto.PeriodLabel.Should().Be("Kỳ 2/3 · Tháng 09/2026");
    }

    [Fact]
    public void An_upcoming_instalment_counts_down_and_a_paid_one_carries_its_invoice()
    {
        FeeScheduleProgress.ToDto(Item(3, 3, Today.AddDays(4), FeeItemStatuses.Pending), Today)
            .Should().Match<Application.DTOs.Finance.ScheduleItemDto>(d => d.DaysUntilDue == 4 && d.DaysOverdue == null);
        // Due today is not late yet.
        FeeScheduleProgress.ToDto(Item(3, 3, Today, FeeItemStatuses.Pending), Today).DaysUntilDue.Should().Be(0);

        var paid = FeeScheduleProgress.ToDto(Item(1, 1, Today.AddDays(-30), FeeItemStatuses.Paid, Now.AddDays(-31)), Today);
        paid.InvoiceNumber.Should().Be("HD-2026-000001");
        (paid.DaysOverdue, paid.DaysUntilDue).Should().Be((null, null));
        paid.PaidAt!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void A_contract_summary_adds_up_paid_owed_overdue_and_the_next_instalment()
    {
        ScheduleItemRow[] items =
        [
            Item(1, 1, Today.AddDays(-30), FeeItemStatuses.Paid, Now.AddDays(-31)),
            Item(2, 2, Today.AddDays(-1), FeeItemStatuses.Pending),
            Item(3, 3, Today.AddDays(29), FeeItemStatuses.Pending),
        ];

        var summary = FeeScheduleProgress.Summarize(Contract, items, Today);

        summary.TotalAmount.Should().Be(3_120_000m);
        summary.PaidAmount.Should().Be(1_040_000m);
        summary.OutstandingAmount.Should().Be(2_080_000m);
        (summary.InstalmentCount, summary.PaidCount, summary.OverdueCount).Should().Be((3, 1, 1));
        summary.NextDue!.FeeItemId.Should().Be(2, "the oldest unpaid instalment is the one to pay next");
    }

    [Fact]
    public async Task Another_vendors_contract_is_not_found()
    {
        var finance = new Mock<IVendorFinanceRepository>();
        finance.Setup(x => x.ListContractsAsync(4, It.IsAny<CancellationToken>())).ReturnsAsync([Contract]);

        var act = () => new GetContractScheduleQueryHandler(VendorContext(), finance.Object, Clock())
            .Handle(new GetContractScheduleQuery(99), default);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task The_statement_keeps_one_vietnamese_calendar_year()
    {
        var finance = new Mock<IFinanceRepository>();
        finance.Setup(x => x.ListInvoicesAsync(4, It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            Invoice(1, new DateTime(2026, 3, 1, 3, 0, 0, DateTimeKind.Utc)),
            // 31/12/2025 18:00 UTC is already 1 January 2026 in Đà Nẵng.
            Invoice(2, new DateTime(2025, 12, 31, 18, 0, 0, DateTimeKind.Utc)),
            Invoice(3, new DateTime(2025, 12, 31, 16, 0, 0, DateTimeKind.Utc)),
        ]);
        var vendorFinance = new Mock<IVendorFinanceRepository>();
        vendorFinance.Setup(x => x.ListContractsAsync(4, It.IsAny<CancellationToken>())).ReturnsAsync([Contract]);
        vendorFinance.Setup(x => x.ListScheduleItemsAsync(4, null, It.IsAny<CancellationToken>())).ReturnsAsync(
            [Item(1, 1, new DateOnly(2026, 9, 1), FeeItemStatuses.Paid, Now), Item(9, 1, new DateOnly(2025, 12, 1), FeeItemStatuses.Paid, Now)]);
        vendorFinance.Setup(x => x.GetVendorNamesAsync(4, It.IsAny<CancellationToken>())).ReturnsAsync(("Phạm Thị Lan", "Bánh mì Cô Lan"));
        var renderer = new Mock<IFinanceDocumentRenderer>();
        renderer.Setup(x => x.RenderVendorStatementXlsx(It.IsAny<VendorStatementExport>())).Returns([1]);

        var file = await new GetVendorStatementQueryHandler(VendorContext(), finance.Object, vendorFinance.Object, renderer.Object, Clock())
            .Handle(new GetVendorStatementQuery(null), default);

        file.FileName.Should().Be("sao-ke-phi-2026.xlsx");
        renderer.Verify(x => x.RenderVendorStatementXlsx(It.Is<VendorStatementExport>(s =>
            s.Year == 2026
            && s.Invoices.Select(i => i.InvoiceId).SequenceEqual(new long[] { 1, 2 })
            && s.Instalments.Single().SlotCode == "NVL-01")));
    }

    private static InvoiceRow Invoice(long id, DateTime issuedAt) =>
        new(id, $"HD-{id}", "FEE", 1_040_000m, issuedAt, id, null, "Kỳ 1/3", "NVL-01", null, "MOMO", issuedAt);

    // ---- Ward: who owes, and how punctually --------------------------------------------------

    private static WardUnpaidItemRow Owed(long id, long contract, DateOnly due, decimal amount = 1_040_000m, int zone = 1) =>
        new(id, contract, 500 + contract, $"Hộ {contract}", "0905000101", $"Quán {contract}", zone, $"Khu {zone}", $"S-{contract:00}", due, amount);

    [Fact]
    public void Debtors_are_contracts_with_something_past_due_most_overdue_first()
    {
        WardUnpaidItemRow[] unpaid =
        [
            Owed(1, contract: 3, Today.AddDays(-10)),
            Owed(2, contract: 3, Today.AddDays(20)),           // upcoming, not overdue
            Owed(3, contract: 7, Today.AddDays(-40), 500_000m),
            Owed(4, contract: 8, Today.AddDays(3)),            // nothing overdue: not a debtor
        ];
        var reminders = new Dictionary<long, DateTime> { [3] = Now.AddHours(-1), [7] = Now.AddDays(-2) };

        var debtors = WardCollectionAnalytics.Debtors(unpaid, Today, reminders);

        debtors.Select(d => d.ContractId).Should().Equal(7, 3);
        var three = debtors[1];
        (three.OverdueCount, three.OverdueAmount, three.UpcomingAmount, three.DaysOverdue).Should().Be((1, 1_040_000m, 1_040_000m, 10));
        three.RemindedToday.Should().BeTrue();
        debtors[0].RemindedToday.Should().BeFalse();
    }

    private static WardFeeItemRow Fee(DateOnly due, string status, DateTime? paidAt = null, bool current = true, int zone = 1) =>
        new(zone, due, 1_000_000m, status, paidAt, current);

    [Fact]
    public void Paid_on_time_means_by_the_end_of_the_due_date_in_vietnam()
    {
        var due = new DateOnly(2026, 9, 20);
        // 23:30 on 20/09 in Đà Nẵng = 16:30 UTC the same day: on time.
        WardCollectionAnalytics.PaidOnTime(Fee(due, FeeItemStatuses.Paid, new DateTime(2026, 9, 20, 16, 30, 0, DateTimeKind.Utc)))
            .Should().BeTrue();
        // 00:30 on 21/09 in Đà Nẵng = 17:30 UTC on 20/09: already late, though still the 20th in UTC.
        WardCollectionAnalytics.PaidOnTime(Fee(due, FeeItemStatuses.Paid, new DateTime(2026, 9, 20, 17, 30, 0, DateTimeKind.Utc)))
            .Should().BeFalse();
        WardCollectionAnalytics.PaidOnTime(Fee(due, FeeItemStatuses.Overdue)).Should().BeFalse();
    }

    [Fact]
    public void Performance_counts_only_current_instalments_as_due_but_all_payments_as_revenue()
    {
        var from = new DateOnly(2026, 9, 1);
        var to = new DateOnly(2026, 9, 30);
        WardFeeItemRow[] activity =
        [
            Fee(new DateOnly(2026, 9, 5), FeeItemStatuses.Paid, new DateTime(2026, 9, 4, 3, 0, 0, DateTimeKind.Utc)),            // on time
            Fee(new DateOnly(2026, 9, 10), FeeItemStatuses.Paid, new DateTime(2026, 9, 15, 3, 0, 0, DateTimeKind.Utc), zone: 2), // late
            Fee(new DateOnly(2026, 9, 25), FeeItemStatuses.Overdue),                                                             // unpaid
            Fee(new DateOnly(2026, 9, 12), FeeItemStatuses.Paid, new DateTime(2026, 9, 11, 3, 0, 0, DateTimeKind.Utc), current: false),
        ];
        WardZoneRow[] zones = [new(1, "Khu 1", 10, 6), new(2, "Khu 2", 5, 5)];

        var performance = WardCollectionAnalytics.Performance(from, to, activity, [Owed(9, 3, Today, zone: 1)], zones);

        (performance.DueCount, performance.PaidOnTimeCount, performance.PaidLateCount, performance.UnpaidCount)
            .Should().Be((3, 1, 1, 1));
        performance.FeeDue.Should().Be(3_000_000m);
        performance.DueCollected.Should().Be(2_000_000m);
        performance.OnTimeRate.Should().BeApproximately(1 / 3d, 0.0001);
        // Zone 1 collected the on-time fee and the superseded one: money received stays revenue.
        performance.ByZone.Single(z => z.ZoneId == 1).FeeCollected.Should().Be(2_000_000m);
        performance.ByZone.Single(z => z.ZoneId == 1).Outstanding.Should().Be(1_040_000m);
        performance.ByZone.Single(z => z.ZoneId == 2).FeeCollected.Should().Be(1_000_000m);
    }

    [Fact]
    public void With_nothing_due_there_is_no_rate_rather_than_a_misleading_zero()
    {
        WardCollectionAnalytics.Performance(Today, Today, [], [], []).OnTimeRate.Should().BeNull();
    }

    [Fact]
    public void The_trend_files_each_payment_under_its_vietnamese_month()
    {
        var months = WardCollectionAnalytics.MonthsEnding(Today, 3);
        months.Should().Equal((2026, 8), (2026, 9), (2026, 10));

        WardFeeItemRow[] activity =
        [
            // 30/09 18:00 UTC is 1 October in Đà Nẵng.
            Fee(new DateOnly(2026, 10, 1), FeeItemStatuses.Paid, new DateTime(2026, 9, 30, 18, 0, 0, DateTimeKind.Utc)),
            Fee(new DateOnly(2026, 9, 10), FeeItemStatuses.Paid, new DateTime(2026, 9, 10, 3, 0, 0, DateTimeKind.Utc)),
        ];
        WardPenaltyPaymentRow[] penalties = [new(500_000m, new DateTime(2026, 8, 14, 3, 0, 0, DateTimeKind.Utc))];

        var trend = WardCollectionAnalytics.Trend(months, activity, penalties);

        trend.Select(m => (m.Month, m.FeeCollected, m.PenaltyCollected)).Should().Equal(
            (8, 0m, 500_000m), (9, 1_000_000m, 0m), (10, 1_000_000m, 0m));
        trend[2].FeeDuePaidOnTime.Should().Be(1_000_000m);
    }

    private static (RemindDebtorCommandHandler Handler, Mock<IWardCollectionRepository> Repo) Reminder(
        WardUnpaidItemRow[] unpaid, DateTime? lastReminder = null)
    {
        var repo = new Mock<IWardCollectionRepository>();
        repo.Setup(x => x.ListUnpaidFeeItemsAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(unpaid);
        repo.Setup(x => x.GetLastDebtRemindersAsync(It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(lastReminder is { } at ? new Dictionary<long, DateTime> { [3] = at } : new Dictionary<long, DateTime>());
        var actor = new Mock<IWardActorContext>();
        actor.Setup(x => x.RequireAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new WardActor(2, 10, "Cán bộ"));
        return (new RemindDebtorCommandHandler(actor.Object, repo.Object, Clock()), repo);
    }

    [Fact]
    public async Task A_reminder_goes_to_the_household_with_the_amount_and_is_audited()
    {
        var (handler, repo) = Reminder([Owed(1, 3, Today.AddDays(-10)), Owed(2, 3, Today.AddDays(-40))]);

        var result = await handler.Handle(new RemindDebtorCommand(3), default);

        result.RemindedAt.Should().Be(Now);
        repo.Verify(x => x.SendDebtReminderAsync(3, 503, 2, It.IsAny<string>(),
            It.Is<string>(body => body.Contains("2 kỳ") && body.Contains("2.080.000") && body.Contains("40 ngày")),
            Now, It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task A_household_can_only_be_reminded_once_a_day()
    {
        var (handler, repo) = Reminder([Owed(1, 3, Today.AddDays(-10))], lastReminder: Now.AddMinutes(-30));

        var act = () => handler.Handle(new RemindDebtorCommand(3), default);

        await act.Should().ThrowAsync<ConflictException>().WithMessage("*10:30*");
        repo.Verify(x => x.SendDebtReminderAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<long>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Nothing_overdue_or_another_wards_contract_cannot_be_reminded()
    {
        var (notDue, _) = Reminder([Owed(1, 3, Today.AddDays(5))]);
        await ((Func<Task>)(() => notDue.Handle(new RemindDebtorCommand(3), default)))
            .Should().ThrowAsync<DomainRuleException>();

        var (elsewhere, _) = Reminder([Owed(1, 3, Today.AddDays(-5))]);
        await ((Func<Task>)(() => elsewhere.Handle(new RemindDebtorCommand(77), default)))
            .Should().ThrowAsync<NotFoundException>();
    }

    // ---- Tracking: "Tôi đang đến" -------------------------------------------------------------

    private static (AnnounceArrivalCommandHandler Handler, Mock<IOrderTrackingRepository> Repo) Arrival(
        string status, DateTime? lastNotified = null)
    {
        var repo = new Mock<IOrderTrackingRepository>();
        repo.Setup(x => x.GetArrivalTargetAsync(42, 6, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ArrivalTargetRow(6, "SB-6", status, 505, lastNotified));
        var customer = new Mock<ICustomerContext>();
        customer.Setup(x => x.RequireCustomerUserIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(42);
        return (new AnnounceArrivalCommandHandler(customer.Object, repo.Object, Mock.Of<IDateTimeProvider>(c => c.UtcNow == Now)), repo);
    }

    [Fact]
    public async Task On_my_way_notifies_the_stall_owner_with_the_eta()
    {
        var (handler, repo) = Arrival(OrderStatuses.Preparing);

        var notice = await handler.Handle(new AnnounceArrivalCommand(6, 7), default);

        notice.AlreadySent.Should().BeFalse();
        repo.Verify(x => x.RecordArrivalAsync(6, 505, "Khách đang đến lấy đơn #SB-6",
            "Khoảng 7 phút nữa khách tới quầy.", Now, It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task A_second_tap_within_two_minutes_does_not_ping_the_stall_again()
    {
        var (handler, repo) = Arrival(OrderStatuses.ReadyForPickup, lastNotified: Now.AddSeconds(-50));

        var notice = await handler.Handle(new AnnounceArrivalCommand(6, null), default);

        notice.AlreadySent.Should().BeTrue();
        notice.NotifiedAt.Should().Be(Now.AddSeconds(-50));
        repo.Verify(x => x.RecordArrivalAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(OrderStatuses.PendingPayment)]
    [InlineData(OrderStatuses.Completed)]
    [InlineData(OrderStatuses.Cancelled)]
    public async Task Only_an_order_still_to_be_collected_can_be_announced(string status)
    {
        var (handler, _) = Arrival(status);

        var act = () => handler.Handle(new AnnounceArrivalCommand(6, null), default);

        await act.Should().ThrowAsync<DomainRuleException>().WithMessage(ArrivalMessages.NotCollectable);
    }

    // ---- shared -------------------------------------------------------------------------------

    private static TimeProvider Clock()
    {
        var clock = new Mock<TimeProvider>();
        clock.Setup(x => x.GetUtcNow()).Returns(new DateTimeOffset(Now));
        return clock.Object;
    }

    private static IVendorContext VendorContext()
    {
        var context = new Mock<IVendorContext>();
        context.Setup(x => x.RequireVendorIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(4);
        return context.Object;
    }
}
