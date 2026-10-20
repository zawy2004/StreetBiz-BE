namespace StreetBiz.Application.Common.Geo;

/// <summary>
/// Where the customer is when they place an order, as the browser's Geolocation API reports it.
/// <see cref="AccuracyMeters"/> is the API's own figure: the radius, in meters, of a circle that
/// holds the true position with 95% confidence.
/// </summary>
public sealed record PickupFix(double Latitude, double Longitude, double AccuracyMeters);

/// <summary>The numbers <see cref="PickupRangeRules"/> applies. Supplied by <c>IPickupRangePolicy</c>.</summary>
public sealed record PickupRangeLimits(
    int RadiusMeters,
    int AccuracyAllowanceMeters,
    int MaxAccuracyMeters);

public enum PickupRangeStatus
{
    /// <summary>Close enough to walk or ride to the stall and collect the order.</summary>
    Within,

    /// <summary>Too far: the order would sit at the stall waiting for a customer who is not coming soon.</summary>
    OutOfRange,

    /// <summary>The position is too vague to tell (IP-based or a cold GPS), so nothing is decided on it.</summary>
    Inaccurate,
}

public sealed record PickupRangeVerdict(
    PickupRangeStatus Status,
    int DistanceMeters,
    int RadiusMeters,
    int AccuracyMeters)
{
    public bool IsWithin => Status == PickupRangeStatus.Within;
}

/// <summary>
/// Orders are pickup-only (there are no shippers), so a customer may only order from a stall they
/// can reach. The rule is a circle around the stall's rented slot, measured as a straight line
/// (Haversine): no routing service, no network call, and the same answer every time for the same
/// inputs.
///
/// GPS noise is handled explicitly instead of ignored:
/// <list type="bullet">
/// <item>A position vaguer than <see cref="PickupRangeLimits.MaxAccuracyMeters"/> is refused as
/// <see cref="PickupRangeStatus.Inaccurate"/>, never guessed at. A Wi-Fi or IP fix can be kilometres
/// off, which is the size of the whole radius.</item>
/// <item>Within that, the customer gets the benefit of the doubt for up to
/// <see cref="PickupRangeLimits.AccuracyAllowanceMeters"/>: a phone standing just inside the line
/// commonly reports itself tens of meters outside it. The allowance is capped, so a vague position
/// cannot stretch the circle by its full error.</item>
/// </list>
/// </summary>
public static class PickupRangeRules
{
    public static PickupRangeVerdict Evaluate(
        double storeLatitude,
        double storeLongitude,
        PickupFix fix,
        PickupRangeLimits limits)
    {
        ArgumentNullException.ThrowIfNull(fix);
        ArgumentNullException.ThrowIfNull(limits);

        var distance = GeoMath.DistanceMeters(storeLatitude, storeLongitude, fix.Latitude, fix.Longitude);
        var accuracy = Math.Max(0, fix.AccuracyMeters);
        var status = Classify(distance, accuracy, limits);

        return new PickupRangeVerdict(
            status,
            (int)Math.Round(distance, MidpointRounding.AwayFromZero),
            limits.RadiusMeters,
            (int)Math.Ceiling(accuracy));
    }

    private static PickupRangeStatus Classify(double distance, double accuracy, PickupRangeLimits limits)
    {
        if (accuracy > limits.MaxAccuracyMeters)
        {
            return PickupRangeStatus.Inaccurate;
        }

        var allowance = Math.Min(accuracy, limits.AccuracyAllowanceMeters);
        return distance - allowance <= limits.RadiusMeters
            ? PickupRangeStatus.Within
            : PickupRangeStatus.OutOfRange;
    }
}
