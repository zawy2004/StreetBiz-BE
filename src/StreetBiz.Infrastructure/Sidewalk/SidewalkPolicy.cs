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
}

public sealed class SidewalkPolicy(IOptions<SidewalkSettings> options) : ISidewalkPolicy
{
    public double AdjacentRadiusMeters => options.Value.AdjacentRadiusMeters;
}
