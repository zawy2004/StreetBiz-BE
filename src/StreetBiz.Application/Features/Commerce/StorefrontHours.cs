using StreetBiz.Application.Common.Models;

namespace StreetBiz.Application.Features.Commerce;

/// <summary>
/// "Open now" rules for storefront discovery. Hours are stored as local wall-clock times, and Vietnam
/// is UTC+7 all year (no daylight saving), so a fixed offset is enough and avoids time-zone database lookups.
/// </summary>
public static class StorefrontHours
{
    private static readonly TimeSpan LocalOffset = TimeSpan.FromHours(7);

    /// <summary>The local weekday (ISO, Monday = 1) and time of day for a UTC instant.</summary>
    public static MarketplaceOpenAt LocalNow(DateTime utcNow)
    {
        var local = utcNow + LocalOffset;
        var day = local.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)local.DayOfWeek;
        return new((short)day, TimeOnly.FromDateTime(local));
    }

    /// <summary>
    /// A storefront that published no hours at all is governed only by its own open/closed switch
    /// (STORE-04), so it counts as open; once hours exist, only a window covering <paramref name="at"/> does.
    /// Opening is inclusive, closing exclusive.
    /// </summary>
    public static bool IsOpen(IReadOnlyList<StorefrontHourRow> hours, MarketplaceOpenAt at) =>
        hours.Count == 0
        || hours.Any(h => h.DayOfWeek == at.DayOfWeek && h.OpensAt <= at.Time && at.Time < h.ClosesAt);

    public static IReadOnlyList<StorefrontHourRow> ForDay(IReadOnlyList<StorefrontHourRow> hours, int dayOfWeek) =>
        hours.Where(h => h.DayOfWeek == dayOfWeek).OrderBy(h => h.OpensAt).ToArray();
}
