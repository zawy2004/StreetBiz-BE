using System.Globalization;
using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Geo;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.Application.Features.Commerce.OrderTracking;

/// <summary>The customer's position as the checkout request carries it.</summary>
public sealed record PickupLocationInput(double Latitude, double Longitude, double AccuracyMeters);

public sealed class PickupLocationInputValidator : AbstractValidator<PickupLocationInput>
{
    /// <summary>Above this a "position" is a country, not a street; such input is a client bug.</summary>
    private const double MaxReportedAccuracyMeters = 100_000;

    public PickupLocationInputValidator()
    {
        RuleFor(x => x.Latitude).InclusiveBetween(-90, 90);
        RuleFor(x => x.Longitude).InclusiveBetween(-180, 180);
        RuleFor(x => x.AccuracyMeters).GreaterThan(0).LessThanOrEqualTo(MaxReportedAccuracyMeters);
    }
}

/// <summary>
/// A request that places a pickup order, and so may only come from within reach of the stall.
/// Implemented by both order-creating commands, so neither the current checkout nor the legacy
/// route can skip the check.
/// </summary>
public interface IPickupRangeGated
{
    /// <summary>The cart being checked out; null means the customer's newest active cart.</summary>
    long? PickupCartId { get; }

    PickupLocationInput? Location { get; }
}

/// <summary>
/// Runs before a gated order command's handler (after validation). Kept out of the handlers so the
/// order workflow itself does not change: an order the range lets through is created exactly as
/// before. Registered per command in <c>DependencyInjection</c>.
///
/// Placed after nothing has been written, it costs one small query and fails fast; the trade-off
/// is that a retry with the same Idempotency-Key is re-checked against where the customer is now.
/// </summary>
public sealed class PickupRangeBehavior<TRequest, TResponse>(
    IPickupRangePolicy policy,
    ICustomerContext customerContext,
    IOrderTrackingRepository tracking)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IPickupRangeGated
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (!policy.Enforced)
        {
            return await next();
        }

        var location = request.Location
            ?? throw new DomainRuleException(PickupRangeMessages.LocationRequired);
        var customerUserId = await customerContext.RequireCustomerUserIdAsync(cancellationToken);
        var point = await tracking.GetCheckoutPickupPointAsync(
            customerUserId, request.PickupCartId, cancellationToken);
        if (point is null)
        {
            // No active cart: the order path reports that in its own words.
            return await next();
        }

        var verdict = PickupRangeRules.Evaluate(
            point.Latitude,
            point.Longitude,
            new PickupFix(location.Latitude, location.Longitude, location.AccuracyMeters),
            policy.Limits);
        if (!verdict.IsWithin)
        {
            throw new DomainRuleException(PickupRangeMessages.Describe(verdict));
        }

        return await next();
    }
}

/// <summary>Shown to buyers as-is by the web client, so they are written in Vietnamese.</summary>
public static class PickupRangeMessages
{
    private static readonly CultureInfo Vietnamese = CultureInfo.GetCultureInfo("vi-VN");

    public const string LocationRequired =
        "Cần vị trí hiện tại của bạn để đặt món. Hãy cho phép định vị rồi thử lại.";

    public static string Describe(PickupRangeVerdict verdict) => verdict.Status switch
    {
        PickupRangeStatus.Inaccurate =>
            $"Vị trí hiện tại chưa đủ chính xác (sai số khoảng {Distance(verdict.AccuracyMeters)}). "
            + "Hãy bật định vị chính xác (GPS) rồi thử lại.",
        PickupRangeStatus.OutOfRange =>
            $"Bạn đang cách quán {Distance(verdict.DistanceMeters)}, vượt phạm vi nhận món "
            + $"{Distance(verdict.RadiusMeters)}. StreetBiz chỉ nhận đơn trong phạm vi này vì bạn "
            + "sẽ tự đến quầy lấy món.",
        _ => string.Empty,
    };

    /// <summary>"650 m", "2 km", "2,4 km": how the app writes distances everywhere else.</summary>
    public static string Distance(int meters) => meters < 1000
        ? $"{meters} m"
        : $"{(meters / 1000d).ToString("0.#", Vietnamese)} km";
}
