using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using StreetBiz.Application;
using StreetBiz.Application.Common.Behaviors;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Geo;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Commerce;
using StreetBiz.Application.Features.Commerce;
using StreetBiz.Application.Features.Commerce.OrderTracking;

namespace StreetBiz.Application.Tests;

/// <summary>ORD-01: a pickup order may only be placed from within reach of the stall.</summary>
public sealed class PickupRangeTests
{
    // A stall on Bạch Đằng, Hải Châu. One degree of latitude is ~111.2 km on the Haversine sphere.
    private const double StoreLat = 16.0600;
    private const double StoreLon = 108.2140;
    private const double MetersPerDegreeLat = 111_195;

    private static readonly PickupRangeLimits Limits = new(
        RadiusMeters: 2000, AccuracyAllowanceMeters: 150, MaxAccuracyMeters: 1000);

    private static PickupFix NorthBy(double meters, double accuracy = 10) =>
        new(StoreLat + meters / MetersPerDegreeLat, StoreLon, accuracy);

    [Fact]
    public void Distance_is_the_great_circle_distance_to_the_stalls_slot()
    {
        var verdict = PickupRangeRules.Evaluate(StoreLat, StoreLon, NorthBy(1500), Limits);

        verdict.DistanceMeters.Should().BeCloseTo(1500, 2);
        verdict.Status.Should().Be(PickupRangeStatus.Within);
        verdict.RadiusMeters.Should().Be(2000);
    }

    [Fact]
    public void Standing_at_the_stall_is_within_range()
    {
        PickupRangeRules.Evaluate(StoreLat, StoreLon, new PickupFix(StoreLat, StoreLon, 5), Limits)
            .Should().Match<PickupRangeVerdict>(v => v.IsWithin && v.DistanceMeters == 0);
    }

    [Fact]
    public void Beyond_the_radius_is_out_of_range()
    {
        PickupRangeRules.Evaluate(StoreLat, StoreLon, NorthBy(3400), Limits)
            .Status.Should().Be(PickupRangeStatus.OutOfRange);
    }

    [Fact]
    public void Reported_gps_error_counts_in_the_customers_favour_up_to_the_allowance()
    {
        // 2,080 m away but the phone itself says it may be 100 m off: possibly inside, so allowed.
        PickupRangeRules.Evaluate(StoreLat, StoreLon, NorthBy(2080, accuracy: 100), Limits)
            .Status.Should().Be(PickupRangeStatus.Within);
        // The same distance with a precise fix is plainly outside.
        PickupRangeRules.Evaluate(StoreLat, StoreLon, NorthBy(2080, accuracy: 5), Limits)
            .Status.Should().Be(PickupRangeStatus.OutOfRange);
    }

    [Fact]
    public void A_vague_position_cannot_stretch_the_circle_by_its_full_error()
    {
        // ±900 m is accepted as a position, but only 150 m of it is credited: 2,400 m is still out.
        PickupRangeRules.Evaluate(StoreLat, StoreLon, NorthBy(2400, accuracy: 900), Limits)
            .Status.Should().Be(PickupRangeStatus.OutOfRange);
    }

    [Fact]
    public void A_position_vaguer_than_the_limit_decides_nothing_even_next_to_the_stall()
    {
        var verdict = PickupRangeRules.Evaluate(StoreLat, StoreLon, NorthBy(50, accuracy: 3000), Limits);

        verdict.Status.Should().Be(PickupRangeStatus.Inaccurate);
        verdict.AccuracyMeters.Should().Be(3000);
    }

    [Theory]
    [InlineData(650, "650 m")]
    [InlineData(999, "999 m")]
    [InlineData(2000, "2 km")]
    [InlineData(2400, "2,4 km")]
    [InlineData(12_350, "12,4 km")]
    public void Distances_are_written_the_way_the_app_writes_them(int meters, string expected) =>
        PickupRangeMessages.Distance(meters).Should().Be(expected);

    [Fact]
    public void The_refusal_names_the_distance_and_the_radius()
    {
        var message = PickupRangeMessages.Describe(
            new PickupRangeVerdict(PickupRangeStatus.OutOfRange, 3400, 2000, 12));

        message.Should().Contain("3,4 km").And.Contain("2 km");
    }

    [Fact]
    public void Checkout_rejects_impossible_coordinates()
    {
        var validator = new CheckoutOrderCommandValidator();

        validator.Validate(new CheckoutOrderCommand(1, "MOMO", "key", new PickupLocationInput(91, 108, 10)))
            .IsValid.Should().BeFalse();
        validator.Validate(new CheckoutOrderCommand(1, "MOMO", "key", new PickupLocationInput(16, 108, 0)))
            .IsValid.Should().BeFalse();
        validator.Validate(new CheckoutOrderCommand(1, "MOMO", "key", new PickupLocationInput(16, 108, 25)))
            .IsValid.Should().BeTrue();
        // Location stays optional at the validation layer; the range rule decides whether it is needed.
        validator.Validate(new CheckoutOrderCommand(1, "MOMO", "key")).IsValid.Should().BeTrue();
    }

    // ---- The gate in front of both order-creating commands ----

    private static readonly PickupPointRow Stall = new(7, "Bún chả Hải Châu", "12 Bạch Đằng", StoreLat, StoreLon);

    private static (PickupRangeBehavior<CheckoutOrderCommand, CheckoutDto> Gate, Mock<IOrderTrackingRepository> Tracking)
        Gate(bool enforced = true, PickupPointRow? point = null)
    {
        var policy = new Mock<IPickupRangePolicy>();
        policy.SetupGet(x => x.Enforced).Returns(enforced);
        policy.SetupGet(x => x.Limits).Returns(Limits);
        var customer = new Mock<ICustomerContext>();
        customer.Setup(x => x.RequireCustomerUserIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(42);
        var tracking = new Mock<IOrderTrackingRepository>();
        tracking.Setup(x => x.GetCheckoutPickupPointAsync(42, It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(point ?? Stall);
        return (new PickupRangeBehavior<CheckoutOrderCommand, CheckoutDto>(policy.Object, customer.Object, tracking.Object),
            tracking);
    }

    private static CheckoutOrderCommand Checkout(PickupLocationInput? location) => new(5, "MOMO", "key-1", location);

    private static PickupLocationInput At(PickupFix fix) => new(fix.Latitude, fix.Longitude, fix.AccuracyMeters);

    private static readonly CheckoutDto Placed = new(1, "SB-1", OrderStatuses.PendingPayment, 2, "MOMO", 30_000, "url");

    [Fact]
    public async Task Within_range_the_order_goes_through_and_is_checked_against_the_carts_stall()
    {
        var (gate, tracking) = Gate();
        var reached = false;

        var result = await gate.Handle(Checkout(At(NorthBy(800))), () => { reached = true; return Task.FromResult(Placed); }, default);

        reached.Should().BeTrue();
        result.Should().Be(Placed);
        tracking.Verify(x => x.GetCheckoutPickupPointAsync(42, 5, It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task Out_of_range_the_order_is_refused_before_anything_is_written()
    {
        var (gate, _) = Gate();
        var reached = false;

        var act = () => gate.Handle(Checkout(At(NorthBy(3400))), () => { reached = true; return Task.FromResult(Placed); }, default);

        await act.Should().ThrowAsync<DomainRuleException>().WithMessage("*3,4 km*2 km*");
        reached.Should().BeFalse();
    }

    [Fact]
    public async Task Without_a_location_the_order_is_refused_while_the_rule_is_enforced()
    {
        var (gate, _) = Gate();

        var act = () => gate.Handle(Checkout(null), () => Task.FromResult(Placed), default);

        await act.Should().ThrowAsync<DomainRuleException>().WithMessage(PickupRangeMessages.LocationRequired);
    }

    [Fact]
    public async Task With_the_rule_off_no_location_is_asked_for()
    {
        var (gate, tracking) = Gate(enforced: false);

        var result = await gate.Handle(Checkout(null), () => Task.FromResult(Placed), default);

        result.Should().Be(Placed);
        tracking.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task With_no_active_cart_the_order_path_reports_it_in_its_own_words()
    {
        var policy = Mock.Of<IPickupRangePolicy>(x => x.Enforced && x.Limits == Limits);
        var customer = new Mock<ICustomerContext>();
        customer.Setup(x => x.RequireCustomerUserIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(42);
        var gate = new PickupRangeBehavior<CheckoutOrderCommand, CheckoutDto>(
            policy, customer.Object, Mock.Of<IOrderTrackingRepository>());
        var reached = false;

        await gate.Handle(Checkout(At(NorthBy(10))), () => { reached = true; return Task.FromResult(Placed); }, default);

        reached.Should().BeTrue();
    }

    [Fact]
    public void The_legacy_order_route_is_gated_on_the_newest_active_cart()
    {
        IPickupRangeGated legacy = new PlacePrepaidOrderCommand("MOMO", "key", new PickupLocationInput(16, 108, 10));
        IPickupRangeGated checkout = new CheckoutOrderCommand(9, "MOMO", "key");

        legacy.PickupCartId.Should().BeNull();
        checkout.PickupCartId.Should().Be(9);
    }

    [Fact]
    public void Both_order_creating_commands_run_the_gate_after_validation()
    {
        var services = new ServiceCollection();
        services.AddApplication();

        static Type[] Pipeline<TRequest, TResponse>(IServiceCollection services) where TRequest : notnull =>
            services.Where(d => d.ServiceType == typeof(IPipelineBehavior<TRequest, TResponse>)
                    || d.ServiceType == typeof(IPipelineBehavior<,>))
                .Select(d => d.ImplementationType!.IsGenericType
                    ? d.ImplementationType.GetGenericTypeDefinition()
                    : d.ImplementationType)
                .ToArray();

        Pipeline<CheckoutOrderCommand, CheckoutDto>(services)
            .Should().ContainInOrder(typeof(ValidationBehavior<,>), typeof(PickupRangeBehavior<,>));
        Pipeline<PlacePrepaidOrderCommand, OrderDto>(services)
            .Should().ContainInOrder(typeof(ValidationBehavior<,>), typeof(PickupRangeBehavior<,>));
    }
}
