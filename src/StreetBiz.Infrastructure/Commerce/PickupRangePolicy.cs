using Microsoft.Extensions.Options;
using StreetBiz.Application.Common.Geo;
using StreetBiz.Application.Common.Interfaces;

namespace StreetBiz.Infrastructure.Commerce;

public sealed class PickupRangeSettings
{
    public const string SectionName = "PickupRange";

    /// <summary>Off only for a demo far from any seeded stall; production keeps it on.</summary>
    public bool Enforced { get; set; } = true;

    /// <summary>
    /// How far a customer may be from the stall when ordering. Pickup-only orders need a customer
    /// who can arrive while the food is still hot: 2 km is roughly a 25-minute walk or a 5-minute
    /// ride in a city centre.
    /// </summary>
    public int RadiusMeters { get; set; } = 2000;

    /// <summary>
    /// Up to this much of the reported GPS error counts in the customer's favour (see
    /// PickupRangeRules). Phone GPS outdoors is typically 5–50 m; 150 m covers a fix taken indoors.
    /// </summary>
    public int AccuracyAllowanceMeters { get; set; } = 150;

    /// <summary>A position vaguer than this decides nothing: IP/Wi-Fi positioning, not GPS.</summary>
    public int MaxAccuracyMeters { get; set; } = 1000;
}

public sealed class PickupRangePolicy(IOptions<PickupRangeSettings> options) : IPickupRangePolicy
{
    public bool Enforced => options.Value.Enforced;

    public PickupRangeLimits Limits => new(
        Math.Max(1, options.Value.RadiusMeters),
        Math.Max(0, options.Value.AccuracyAllowanceMeters),
        Math.Max(1, options.Value.MaxAccuracyMeters));
}
