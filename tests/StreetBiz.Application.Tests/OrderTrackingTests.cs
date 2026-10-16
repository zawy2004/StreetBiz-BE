using FluentAssertions;
using Moq;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.Commerce.OrderTracking;

namespace StreetBiz.Application.Tests;

/// <summary>ORD-02: "when will my order be ready, and how many are ahead of me?".</summary>
public sealed class OrderTrackingTests
{
    // ---- The estimate itself ----

    [Fact]
    public void Percentiles_interpolate_between_the_nearest_ranks()
    {
        double[] sorted = [1, 2, 3, 4, 5, 6, 7, 8, 9];

        PrepTimeEstimator.Percentile(sorted, 0.25).Should().Be(3);
        PrepTimeEstimator.Percentile(sorted, 0.50).Should().Be(5);
        PrepTimeEstimator.Percentile(sorted, 0.75).Should().Be(7);
        PrepTimeEstimator.Percentile([10, 20], 0.5).Should().Be(15);
    }

    [Fact]
    public void A_stall_with_enough_history_gets_the_middle_half_of_its_own_prep_times()
    {
        var estimate = PrepTimeEstimator.Estimate([8, 9, 10, 11, 12, 13, 14, 15, 16]);

        estimate.Should().Be(new PrepTimeEstimate(10, 12, 14, PrepTimeBases.History, 9));
    }

    [Fact]
    public void One_forgotten_ready_tap_does_not_move_the_estimate()
    {
        double[] normal = [8, 9, 10, 11, 12, 13, 14];

        var withOutlier = PrepTimeEstimator.Estimate([.. normal, 95]);
        var withoutOutlier = PrepTimeEstimator.Estimate(normal);

        // A mean would jump from 11 to ~21.5 minutes; the median moves by half a minute.
        withOutlier.TypicalMinutes.Should().BeInRange(withoutOutlier.TypicalMinutes, withoutOutlier.TypicalMinutes + 1);
    }

    [Fact]
    public void Implausible_durations_are_dropped_before_counting()
    {
        // Two real samples plus noise (clock skew, back-to-back taps, forgotten "ready"): not enough
        // left to measure, so the default stands in.
        var estimate = PrepTimeEstimator.Estimate([-3, 0, 0.05, 0.4, 12, 14, 400, 600]);

        estimate.Basis.Should().Be(PrepTimeBases.Default);
        estimate.SampleSize.Should().Be(2);
        (estimate.LowMinutes, estimate.HighMinutes).Should().Be((10, 20));
    }

    [Fact]
    public void Identical_samples_give_a_single_figure_not_an_invented_range()
    {
        var estimate = PrepTimeEstimator.Estimate([7, 7, 7, 7, 7]);

        (estimate.LowMinutes, estimate.TypicalMinutes, estimate.HighMinutes).Should().Be((7, 7, 7));
    }

    // ---- The query ----

    private static readonly DateTime Now = new(2026, 10, 2, 4, 0, 0, DateTimeKind.Utc);
    private static readonly PickupPointRow Stall = new(7, "Bún chả Hải Châu", "12 Bạch Đằng", 16.06, 108.214);

    private static (GetOrderTrackingQueryHandler Handler, Mock<IOrderTrackingRepository> Tracking) Handler(
        OrderTrackingRow? order, IReadOnlyList<double>? samples = null)
    {
        var customer = new Mock<ICustomerContext>();
        customer.Setup(x => x.RequireCustomerUserIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(42);
        var tracking = new Mock<IOrderTrackingRepository>();
        tracking.Setup(x => x.GetOrderTrackingAsync(42, 3, It.IsAny<CancellationToken>())).ReturnsAsync(order);
        tracking.Setup(x => x.GetRecentPrepMinutesAsync(7, It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(samples ?? [8, 9, 10, 11, 12, 13, 14, 15, 16]);
        var clock = Mock.Of<IDateTimeProvider>(x => x.UtcNow == Now);
        return (new GetOrderTrackingQueryHandler(customer.Object, tracking.Object, clock), tracking);
    }

    private static OrderTrackingRow Order(string status, DateTime? acceptedAt = null, int ahead = 0, DateTime? readyAt = null) =>
        new(3, status, Now.AddMinutes(-20), acceptedAt, readyAt, Stall, ahead);

    [Fact]
    public async Task Another_customers_order_is_not_found()
    {
        var (handler, _) = Handler(null);

        var act = () => handler.Handle(new GetOrderTrackingQuery(3), default);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task An_accepted_order_gets_a_ready_window_counted_from_acceptance()
    {
        var accepted = Now.AddMinutes(-4);
        var (handler, tracking) = Handler(Order(OrderStatuses.Preparing, accepted, ahead: 2));

        var result = await handler.Handle(new GetOrderTrackingQuery(3), default);

        result.ReadyEstimate!.EarliestReadyAt.Should().Be(accepted.AddMinutes(10));
        result.ReadyEstimate.LatestReadyAt.Should().Be(accepted.AddMinutes(14));
        result.ReadyEstimate.IsLate.Should().BeFalse();
        result.OrdersAhead.Should().Be(2);
        result.PickupPoint.Latitude.Should().Be(16.06);
        tracking.Verify(x => x.GetRecentPrepMinutesAsync(
            7, Now.AddDays(-PrepTimeEstimator.LookbackDays), PrepTimeEstimator.MaxSamples, It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task Past_the_window_the_order_is_flagged_late_rather_than_given_a_negative_wait()
    {
        var (handler, _) = Handler(Order(OrderStatuses.Preparing, Now.AddMinutes(-30)));

        var result = await handler.Handle(new GetOrderTrackingQuery(3), default);

        result.ReadyEstimate!.IsLate.Should().BeTrue();
    }

    [Fact]
    public async Task Before_the_stall_accepts_only_the_usual_duration_is_given_not_a_clock_time()
    {
        var (handler, _) = Handler(Order(OrderStatuses.Placed));

        var result = await handler.Handle(new GetOrderTrackingQuery(3), default);

        result.ReadyEstimate!.LowMinutes.Should().Be(10);
        result.ReadyEstimate.EarliestReadyAt.Should().BeNull();
        result.ReadyEstimate.LatestReadyAt.Should().BeNull();
    }

    [Theory]
    [InlineData(OrderStatuses.PendingPayment)]
    [InlineData(OrderStatuses.ReadyForPickup)]
    [InlineData(OrderStatuses.Completed)]
    [InlineData(OrderStatuses.Cancelled)]
    [InlineData(OrderStatuses.Rejected)]
    public async Task Outside_preparation_there_is_no_estimate_and_no_queue(string status)
    {
        var (handler, tracking) = Handler(Order(status, ahead: 3));

        var result = await handler.Handle(new GetOrderTrackingQuery(3), default);

        result.ReadyEstimate.Should().BeNull();
        result.OrdersAhead.Should().Be(0);
        tracking.Verify(x => x.GetRecentPrepMinutesAsync(
            It.IsAny<long>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
