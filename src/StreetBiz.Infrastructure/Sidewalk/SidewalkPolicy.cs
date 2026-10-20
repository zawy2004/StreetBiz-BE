using Microsoft.Extensions.Options;
using StreetBiz.Application.Common.Interfaces;

namespace StreetBiz.Infrastructure.Sidewalk;

public sealed class SidewalkSettings
{
    public const string SectionName = "Sidewalk";

    /// <summary>
    /// BR-11 (SIDE-03A). Not per-ward: the database has no column for that, and this schema
    /// must not gain a migration to add one. Default chosen as a reasonable walking distance
    /// from a storefront to a curb-side slot.
    /// </summary>
    public double AdjacentRadiusMeters { get; set; } = 150;

    /// <summary>How long a slot hold reserves the slot before it silently lapses.</summary>
    public int SlotHoldTtlMinutes { get; set; } = 15;

    /// <summary>How many slots one registration may hold at the same time.</summary>
    public int MaxSlotHoldsPerRegistration { get; set; } = 3;

    /// <summary>
    /// WARD-01 clearance warnings between a slot and street features. Off by default: no
    /// regulation fixing these distances for vendor stalls was found, so they are operational
    /// values the ward confirms before enabling, never shown as a legal standard. Warnings only;
    /// the hard block comes from StreetFeatures.blocks_business.
    /// </summary>
    public bool FeatureClearanceEnabled { get; set; }

    public Dictionary<string, double> FeatureClearanceMeters { get; set; } = new()
    {
        ["HYDRANT"] = 5,
        ["BUS_STOP"] = 5,
        ["TRANSFORMER"] = 3,
    };
}

public sealed class SidewalkPolicy(IOptions<SidewalkSettings> options) : ISidewalkPolicy
{
    public double AdjacentRadiusMeters => options.Value.AdjacentRadiusMeters;

    public int SlotHoldTtlMinutes => options.Value.SlotHoldTtlMinutes;

    public int MaxSlotHoldsPerRegistration => options.Value.MaxSlotHoldsPerRegistration;
}
