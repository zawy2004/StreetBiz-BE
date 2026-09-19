using StreetBiz.Application.Common.Security;

namespace StreetBiz.Application.Common.Models;

public sealed record SalesPeriodWindow(DateTime FromUtc, DateTime ToUtc);

public static class SalesPeriodWindowCalculator
{
    public static SalesPeriodWindow Calculate(
        string period,
        DateTimeOffset nowUtc,
        TimeZoneInfo businessTimeZone)
    {
        var localNow = TimeZoneInfo.ConvertTime(nowUtc, businessTimeZone);
        var localStart = period switch
        {
            SalesPeriods.Day => localNow.Date,
            SalesPeriods.Week => localNow.Date.AddDays(-(((int)localNow.DayOfWeek + 6) % 7)),
            SalesPeriods.Month => new DateTime(localNow.Year, localNow.Month, 1),
            _ => throw new ArgumentOutOfRangeException(nameof(period), period, "Unsupported sales period."),
        };
        var fromUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(localStart, DateTimeKind.Unspecified),
            businessTimeZone);
        return new SalesPeriodWindow(fromUtc, nowUtc.UtcDateTime);
    }
}
