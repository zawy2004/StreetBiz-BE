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
}

public sealed class SidewalkPolicy(IOptions<SidewalkSettings> options) : ISidewalkPolicy
{
    public double AdjacentRadiusMeters => options.Value.AdjacentRadiusMeters;

    public int SlotHoldTtlMinutes => options.Value.SlotHoldTtlMinutes;

    public int MaxSlotHoldsPerRegistration => options.Value.MaxSlotHoldsPerRegistration;
}
