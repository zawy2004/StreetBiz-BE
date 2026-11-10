using FluentAssertions;
using Moq;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.PlatformAdministration;
using StreetBiz.Application.Features.PlatformAdministration;

namespace StreetBiz.Application.Tests;

/// <summary>ADM-06: the arithmetic and the period rules, without a database.</summary>
public sealed class PlatformDashboardTests
{
    private static readonly DashboardPeriodRow Empty = new(0, 0, 0, 0, 0, 0m, 0, 0, null);
    private static readonly DashboardSnapshotRow NoSnapshot = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0m);

    private static PlatformDashboardFacts Facts(DashboardPeriodRow? current = null, DashboardPeriodRow? previous = null) =>
        new(current ?? Empty, previous ?? Empty, NoSnapshot, [], [], []);

    private static readonly DateOnly From = new(2026, 10, 1);
    private static readonly DateOnly To = new(2026, 10, 3);

    [Fact]
    public void An_empty_period_is_all_zeroes_with_nothing_to_compare()
    {
        var dto = PlatformDashboardBuilder.Build(From, To, From.AddDays(-3), From.AddDays(-1), Facts());

        dto.Orders.Placed.Should().Be(new DashboardMetricDto(0, 0, null));
        dto.Orders.Gmv.ChangePercent.Should().BeNull();
        dto.Orders.CancelRejectRate.Should().Be(new DashboardMetricDto(0, 0, null));
        dto.Users.NewUsers.ChangePercent.Should().BeNull();
        dto.Moderation.AverageReviewHours.Should().BeNull();
        dto.TopStorefronts.Should().BeEmpty();
    }

    [Fact]
    public void Every_day_of_the_period_gets_a_point_even_when_nothing_happened()
    {
        var facts = Facts() with
        {
            Days =
            [
                new DashboardDayRow(new DateOnly(2026, 10, 2), 4, 0, 0m),
                new DashboardDayRow(new DateOnly(2026, 10, 2), 0, 2, 150_000m),
            ],
        };

        var dto = PlatformDashboardBuilder.Build(From, To, From.AddDays(-3), From.AddDays(-1), facts);

        dto.OrdersByDay.Select(d => d.Day).Should().Equal(From, From.AddDays(1), To);
        dto.OrdersByDay[0].Should().Be(new DashboardDayDto(From, 0, 0, 0m));
        dto.OrdersByDay[1].Should().Be(new DashboardDayDto(From.AddDays(1), 4, 2, 150_000m));
    }

    [Fact]
    public void A_previous_period_of_zero_gives_no_percentage_rather_than_infinity()
    {
        var current = Empty with { OrdersPlaced = 10, OrdersCompleted = 5, Gmv = 500_000m, NewUsers = 3 };

        var dto = PlatformDashboardBuilder.Build(From, To, From.AddDays(-3), From.AddDays(-1), Facts(current));

        dto.Orders.Placed.Should().Be(new DashboardMetricDto(10, 0, null));
        dto.Orders.Gmv.ChangePercent.Should().BeNull();
        dto.Users.NewUsers.ChangePercent.Should().BeNull();
    }

    [Fact]
    public void Changes_are_measured_against_the_previous_period()
    {
        var current = Empty with { OrdersPlaced = 15, Gmv = 750_000m };
        var previous = Empty with { OrdersPlaced = 10, Gmv = 1_000_000m };

        var dto = PlatformDashboardBuilder.Build(From, To, From.AddDays(-3), From.AddDays(-1), Facts(current, previous));

        dto.Orders.Placed.ChangePercent.Should().Be(50m);
        dto.Orders.Gmv.ChangePercent.Should().Be(-25m);
    }

    [Fact]
    public void The_cancel_and_reject_rate_is_a_share_of_orders_placed()
    {
        var current = Empty with { OrdersPlaced = 8, OrdersCancelled = 1, OrdersRejected = 1 };
        var previous = Empty with { OrdersPlaced = 10, OrdersCancelled = 1 };

        var dto = PlatformDashboardBuilder.Build(From, To, From.AddDays(-3), From.AddDays(-1), Facts(current, previous));

        dto.Orders.CancelRejectRate.Value.Should().Be(25m);
        dto.Orders.CancelRejectRate.Previous.Should().Be(10m);
        dto.Orders.CancelRejectRate.ChangePercent.Should().Be(150m);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 0, 0)]
    [InlineData(1, 3, 33.3)]
    public void Rates_never_divide_by_zero(int part, int whole, double expected) =>
        PlatformDashboardBuilder.Rate(part, whole).Should().Be((decimal)expected);

    [Fact]
    public void The_default_period_is_the_last_thirty_days_and_the_one_before_it()
    {
        var (from, to, previousFrom, previousTo) =
            DashboardPeriods.Resolve(null, null, new DateOnly(2026, 10, 30));

        from.Should().Be(new DateOnly(2026, 10, 1));
        to.Should().Be(new DateOnly(2026, 10, 30));
        previousTo.Should().Be(new DateOnly(2026, 9, 30));
        previousFrom.Should().Be(new DateOnly(2026, 9, 1));
    }

    [Fact]
    public void The_period_validator_rejects_reversed_and_oversized_ranges()
    {
        var validator = new GetPlatformDashboardQueryValidator();

        validator.Validate(new GetPlatformDashboardQuery(To, From)).IsValid.Should().BeFalse();
        validator.Validate(new GetPlatformDashboardQuery(From, From.AddDays(366))).IsValid.Should().BeFalse();
        validator.Validate(new GetPlatformDashboardQuery(From, From.AddDays(365))).IsValid.Should().BeTrue();
        validator.Validate(new GetPlatformDashboardQuery(null, null)).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task The_handler_asks_the_repository_for_the_resolved_period_in_vietnam_time()
    {
        var actor = new Mock<IPlatformAdminContext>();
        actor.Setup(x => x.RequireAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlatformAdminActor(7, "Admin"));
        var repository = new Mock<IPlatformAdministrationRepository>();
        repository.Setup(x => x.GetDashboardFactsAsync(It.IsAny<DashboardRange>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Facts());
        var clock = new Mock<IDateTimeProvider>();
        // 20:00 UTC on 29 Oct is already 30 Oct in Da Nang.
        clock.SetupGet(c => c.UtcNow).Returns(new DateTime(2026, 10, 29, 20, 0, 0, DateTimeKind.Utc));

        var dto = await new GetPlatformDashboardQueryHandler(actor.Object, repository.Object, clock.Object)
            .Handle(new GetPlatformDashboardQuery(null, null), default);

        dto.To.Should().Be(new DateOnly(2026, 10, 30));
        dto.OrdersByDay.Should().HaveCount(30);
        repository.Verify(x => x.GetDashboardFactsAsync(
            new DashboardRange(
                new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 30),
                new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_non_admin_gets_no_figures()
    {
        var actor = new Mock<IPlatformAdminContext>();
        actor.Setup(x => x.RequireAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new ForbiddenException("no"));
        var repository = new Mock<IPlatformAdministrationRepository>();

        var act = () => new GetPlatformDashboardQueryHandler(actor.Object, repository.Object, Mock.Of<IDateTimeProvider>())
            .Handle(new GetPlatformDashboardQuery(null, null), default);

        await act.Should().ThrowAsync<ForbiddenException>();
        repository.VerifyNoOtherCalls();
    }
}
