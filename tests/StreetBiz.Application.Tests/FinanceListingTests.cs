using FluentAssertions;
using Moq;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.Finance.GetSummary;
using StreetBiz.Application.Features.Finance.ListFeeItems;
using StreetBiz.Application.Features.Finance.ListPayments;
using StreetBiz.Application.Features.Finance.ListPenalties;
using StreetBiz.Application.Features.Finance.ListViolations;
using StreetBiz.Application.Features.Finance.PenaltySchedules;

namespace StreetBiz.Application.Tests;

public sealed class FinanceListingTests
{
    [Fact]
    public async Task Summary_combines_fee_and_penalty_due_into_the_total()
    {
        var finance = new Mock<IFinanceRepository>();
        finance.Setup(x => x.GetSummaryAsync(4, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FinanceSummaryRow(6_040_000m, 1_000_000m, 1, new DateOnly(2026, 10, 20)));

        var dto = await new GetFinanceSummaryQueryHandler(VendorContext(), finance.Object)
            .Handle(new GetFinanceSummaryQuery(), CancellationToken.None);

        dto.TotalDue.Should().Be(7_040_000m);
        dto.OverdueCount.Should().Be(1);
        dto.NextDueDate.Should().Be(new DateOnly(2026, 10, 20));
    }

    [Fact]
    public async Task List_fee_items_passes_the_status_filter_through_unchanged()
    {
        var finance = new Mock<IFinanceRepository>();
        finance.Setup(x => x.ListFeeItemsAsync(4, FeeItemStatuses.Overdue, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new FeeItemListRow(2, 1, "NVL-01", 2, 3, new DateOnly(2026, 9, 20), 1_040_000m, FeeItemStatuses.Overdue, null)]);

        var items = await new ListFeeItemsQueryHandler(VendorContext(), finance.Object)
            .Handle(new ListFeeItemsQuery(FeeItemStatuses.Overdue), CancellationToken.None);

        items.Should().ContainSingle();
        items[0].ItemStatus.Should().Be(FeeItemStatuses.Overdue);
        items[0].PeriodLabel.Should().Be("Kỳ 2/3 · Tháng 09/2026");
    }

    [Fact]
    public async Task A_lower_case_status_filter_reaches_the_repository_normalized()
    {
        // The fee filter runs in memory (case-sensitive) and the penalty filter in SQL Server
        // (case-insensitive collation); normalizing up front keeps both endpoints consistent.
        var finance = new Mock<IFinanceRepository>();
        finance.Setup(x => x.ListFeeItemsAsync(4, FeeItemStatuses.Paid, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        finance.Setup(x => x.ListPenaltiesAsync(4, PenaltyStatuses.Unpaid, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        await new ListFeeItemsQueryHandler(VendorContext(), finance.Object)
            .Handle(new ListFeeItemsQuery(" paid "), CancellationToken.None);
        await new ListPenaltiesQueryHandler(VendorContext(), finance.Object)
            .Handle(new ListPenaltiesQuery("unpaid"), CancellationToken.None);

        finance.Verify(x => x.ListFeeItemsAsync(4, FeeItemStatuses.Paid, It.IsAny<CancellationToken>()));
        finance.Verify(x => x.ListPenaltiesAsync(4, PenaltyStatuses.Unpaid, It.IsAny<CancellationToken>()));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("overdue", true)]
    [InlineData("PAYED", false)]
    public void An_unknown_fee_status_is_a_validation_error_not_an_empty_list(string? status, bool valid) =>
        new ListFeeItemsQueryValidator().Validate(new ListFeeItemsQuery(status)).IsValid.Should().Be(valid);

    [Theory]
    [InlineData("waived", true)]
    [InlineData("PENDING", false)]
    public void An_unknown_penalty_status_is_a_validation_error_not_an_empty_list(string status, bool valid) =>
        new ListPenaltiesQueryValidator().Validate(new ListPenaltiesQuery(status)).IsValid.Should().Be(valid);

    [Fact]
    public async Task List_penalties_maps_every_field_the_screen_needs()
    {
        var finance = new Mock<IFinanceRepository>();
        finance.Setup(x => x.ListPenaltiesAsync(4, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new PenaltyListRow(
                2, 2, "BLOCK_PEDESTRIAN", "Cản trở lối đi bộ", "NVL-08", 1_000_000m,
                PenaltyStatuses.Unpaid, new DateTime(2026, 9, 17, 2, 8, 19, DateTimeKind.Utc), null)]);

        var penalties = await new ListPenaltiesQueryHandler(VendorContext(), finance.Object)
            .Handle(new ListPenaltiesQuery(null), CancellationToken.None);

        penalties.Should().ContainSingle();
        penalties[0].ViolationLabel.Should().Be("Cản trở lối đi bộ");
        penalties[0].PenaltyStatus.Should().Be(PenaltyStatuses.Unpaid);
        penalties[0].IssuedAt.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public async Task Payment_history_is_scoped_to_the_caller_s_own_vendor()
    {
        var finance = new Mock<IFinanceRepository>();
        finance.Setup(x => x.ListPaymentTransactionsAsync(4, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new PaymentTransactionRow(
                1, PaymentPurposes.RentalFee, "MOMO", 1_540_000m, PaymentStatuses.Success,
                "Kỳ 1/3 · Tháng 08/2026", "NVL-01", DateTime.UtcNow, DateTime.UtcNow)]);

        var payments = await new ListPaymentTransactionsQueryHandler(VendorContext(), finance.Object)
            .Handle(new ListPaymentTransactionsQuery(), CancellationToken.None);

        payments.Should().ContainSingle();
        finance.Verify(x => x.ListPaymentTransactionsAsync(4, It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task Violation_list_is_scoped_to_the_caller_s_own_vendor()
    {
        var finance = new Mock<IFinanceRepository>();
        finance.Setup(x => x.ListVendorViolationsAsync(4, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        await new ListVendorViolationsQueryHandler(VendorContext(), finance.Object)
            .Handle(new ListVendorViolationsQuery(), CancellationToken.None);

        finance.Verify(x => x.ListVendorViolationsAsync(4, It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task Penalty_rates_are_grouped_by_ward_for_today_in_vietnam()
    {
        // 18:00 UTC on 31 December is already 1 January in Đà Nẵng: the new year's rates apply.
        var clock = new Mock<IDateTimeProvider>();
        clock.SetupGet(c => c.UtcNow).Returns(new DateTime(2026, 12, 31, 18, 0, 0, DateTimeKind.Utc));
        var finance = new Mock<IFinanceRepository>();
        finance.Setup(x => x.ListWardPenaltyRatesAsync(4, new DateOnly(2027, 1, 1), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new WardPenaltyRateRow(1, "Phường Hải Châu", "OUTSIDE_SLOT", "Bán ngoài ô", 500_000m, "NĐ 123"),
                new WardPenaltyRateRow(1, "Phường Hải Châu", "NO_PERMIT", "Không có giấy phép", 1_500_000m, null),
                new WardPenaltyRateRow(2, "Phường Thanh Khê", "OUTSIDE_SLOT", "Bán ngoài ô", 700_000m, null),
            ]);

        var wards = await new ListVendorPenaltySchedulesQueryHandler(VendorContext(), finance.Object, clock.Object)
            .Handle(new ListVendorPenaltySchedulesQuery(), CancellationToken.None);

        wards.Select(w => (w.WardName, w.Rates.Count)).Should().Equal(("Phường Hải Châu", 2), ("Phường Thanh Khê", 1));
        wards[0].Rates[1].Should().Be(new StreetBiz.Application.DTOs.Finance.VendorPenaltyRateDto("NO_PERMIT", "Không có giấy phép", 1_500_000m, null));
    }

    private static IVendorContext VendorContext()
    {
        var context = new Mock<IVendorContext>();
        context.Setup(x => x.RequireVendorIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(4);
        return context.Object;
    }
}
