using FluentAssertions;
using Moq;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Features.Finance.WardReports;
using StreetBiz.Application.Features.WardSlots;

namespace StreetBiz.Application.Tests;

public sealed class WardReportTests
{
    [Fact]
    public async Task Collection_report_defaults_to_the_first_of_the_month_through_today()
    {
        var reports = new Mock<IWardReportRepository>();
        reports.Setup(x => x.GetCollectionReportAsync(
                10, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 23), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CollectionReportRow(0, 0, 0, 0, 0, 0, []));
        var clock = FixedClock(new DateTime(2026, 9, 23, 8, 0, 0, DateTimeKind.Utc));

        var dto = await new GetCollectionReportQueryHandler(WardActorContext(10), reports.Object, clock)
            .Handle(new GetCollectionReportQuery(null, null), CancellationToken.None);

        dto.From.Should().Be(new DateOnly(2026, 9, 1));
        dto.To.Should().Be(new DateOnly(2026, 9, 23));
    }

    [Fact]
    public async Task Collection_report_honours_an_explicit_period()
    {
        var reports = new Mock<IWardReportRepository>();
        reports.Setup(x => x.GetCollectionReportAsync(
                10, new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 31), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CollectionReportRow(1_000_000, 0, 0, 0, 0, 1, []));

        var dto = await new GetCollectionReportQueryHandler(
                WardActorContext(10), reports.Object, FixedClock(new DateTime(2026, 9, 23, 0, 0, 0, DateTimeKind.Utc)))
            .Handle(new GetCollectionReportQuery(new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 31)), CancellationToken.None);

        dto.FeeCollected.Should().Be(1_000_000);
        dto.InvoiceCount.Should().Be(1);
    }

    [Fact]
    public async Task Collection_report_always_scopes_to_the_caller_s_own_ward()
    {
        var reports = new Mock<IWardReportRepository>();
        reports.Setup(x => x.GetCollectionReportAsync(
                11, It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CollectionReportRow(0, 0, 0, 0, 0, 0, []));

        await new GetCollectionReportQueryHandler(
                WardActorContext(11), reports.Object, FixedClock(DateTime.UtcNow))
            .Handle(new GetCollectionReportQuery(null, null), CancellationToken.None);

        reports.Verify(x => x.GetCollectionReportAsync(
            11, It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()));
    }

    [Theory]
    [InlineData(23, 3, 13)]
    [InlineData(0, 0, 0)]
    [InlineData(4, 2, 50)]
    [InlineData(3, 1, 33)]
    public async Task Dashboard_computes_occupancy_percent_without_dividing_by_zero(
        int slotTotal, int slotRented, int expectedPercent)
    {
        var reports = new Mock<IWardReportRepository>();
        reports.Setup(x => x.GetDashboardAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WardDashboardRow(slotTotal, slotRented, 0, 0, 0, 0, 0, 0));

        var dto = await new GetWardDashboardQueryHandler(WardActorContext(10), reports.Object)
            .Handle(new GetWardDashboardQuery(), CancellationToken.None);

        dto.OccupancyPercent.Should().Be(expectedPercent);
    }

    private static TimeProvider FixedClock(DateTime utcNow)
    {
        var clock = new Mock<TimeProvider>();
        clock.Setup(x => x.GetUtcNow()).Returns(new DateTimeOffset(utcNow, TimeSpan.Zero));
        return clock.Object;
    }

    private static IWardActorContext WardActorContext(int wardId)
    {
        var context = new Mock<IWardActorContext>();
        context.Setup(x => x.RequireAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WardActor(2, wardId, "Cán bộ phường"));
        return context.Object;
    }
}
