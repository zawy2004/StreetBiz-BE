namespace StreetBiz.Application.Features.Commerce.OrderTracking;

/// <summary>
/// How long a stall usually takes from accepting an order to marking it ready, as a range.
/// <see cref="LowMinutes"/>–<see cref="HighMinutes"/> is the middle half of its recent orders
/// (25th to 75th percentile), <see cref="TypicalMinutes"/> the median.
/// </summary>
public sealed record PrepTimeEstimate(
    int LowMinutes,
    int TypicalMinutes,
    int HighMinutes,
    string Basis,
    int SampleSize);

public static class PrepTimeBases
{
    /// <summary>Measured from this stall's own recent orders.</summary>
    public const string History = "HISTORY";

    /// <summary>Too few orders to measure; a platform-wide default stands in.</summary>
    public const string Default = "DEFAULT";
}

/// <summary>
/// ORD-02 tracking: "when will it be ready?". Delivery apps estimate preparation time from each
/// merchant's own history rather than a fixed figure; a stall here makes a handful of dishes, so
/// a robust summary of its recent orders is enough and needs no model.
///
/// <list type="bullet">
/// <item><b>A range, not a point.</b> Street food prep varies with the queue and the dish. Showing
/// the interquartile range says how sure the estimate is, the way maps show "usually 10–15 min".</item>
/// <item><b>Percentiles, not the mean.</b> One order the vendor forgot to mark ready for an hour
/// would drag a mean far off; the 25th/50th/75th percentiles barely move.</item>
/// <item><b>Implausible samples dropped.</b> Nothing under <see cref="MinPlausibleMinutes"/> (the
/// vendor tapping accept → ready back to back, not cooking) and nothing over
/// <see cref="MaxPlausibleMinutes"/> (a forgotten "ready" tap). A stall that only sells ready-made
/// items therefore gets the default range: over-estimating is the safe side for a pickup, since the
/// food is waiting when the customer arrives.</item>
/// <item><b>No queue term added.</b> The history was measured under the stall's real load, so the
/// queue is already inside it; adding "orders ahead × something" would count it twice. The number
/// of orders ahead is reported separately, as information.</item>
/// </list>
/// </summary>
public static class PrepTimeEstimator
{
    /// <summary>Fewer measured orders than this and the percentiles are noise.</summary>
    public const int MinimumSamples = 5;

    public const double MinPlausibleMinutes = 1;

    public const double MaxPlausibleMinutes = 120;

    /// <summary>Most recent orders considered; older behaviour says less about today's stall.</summary>
    public const int MaxSamples = 50;

    public const int LookbackDays = 30;

    public static readonly PrepTimeEstimate Default = new(10, 15, 20, PrepTimeBases.Default, 0);

    public static PrepTimeEstimate Estimate(IEnumerable<double> prepMinutes)
    {
        ArgumentNullException.ThrowIfNull(prepMinutes);

        var samples = prepMinutes
            .Where(minutes => minutes >= MinPlausibleMinutes && minutes <= MaxPlausibleMinutes)
            .Order()
            .ToArray();
        if (samples.Length < MinimumSamples)
        {
            return Default with { SampleSize = samples.Length };
        }

        var low = Math.Max(1, (int)Math.Floor(Percentile(samples, 0.25)));
        var typical = Math.Max(low, (int)Math.Round(Percentile(samples, 0.50), MidpointRounding.AwayFromZero));
        var high = Math.Max(typical, (int)Math.Ceiling(Percentile(samples, 0.75)));
        return new PrepTimeEstimate(low, typical, high, PrepTimeBases.History, samples.Length);
    }

    /// <summary>
    /// Linear interpolation between the two nearest ranks (the "inclusive" method of Excel's
    /// PERCENTILE.INC and NumPy's default), over an ascending, non-empty array.
    /// </summary>
    public static double Percentile(IReadOnlyList<double> sorted, double fraction)
    {
        ArgumentNullException.ThrowIfNull(sorted);
        if (sorted.Count == 0)
        {
            throw new ArgumentException("At least one sample is required.", nameof(sorted));
        }

        var rank = Math.Clamp(fraction, 0, 1) * (sorted.Count - 1);
        var below = (int)Math.Floor(rank);
        var above = (int)Math.Ceiling(rank);
        return sorted[below] + (sorted[above] - sorted[below]) * (rank - below);
    }
}
