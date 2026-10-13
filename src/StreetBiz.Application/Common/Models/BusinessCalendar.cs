namespace StreetBiz.Application.Common.Models;

/// <summary>
/// Đà Nẵng's calendar (UTC+7, no daylight saving). Due dates, "today" and report periods are
/// Vietnamese calendar days while timestamps are stored in UTC, so a day boundary is 17:00 UTC of
/// the previous day, not UTC midnight. A fixed-offset zone rather than FindSystemTimeZoneById for
/// the same reason WardSlots uses one: it cannot be missing from the host's time zone database.
/// </summary>
public static class BusinessCalendar
{
    public static readonly TimeZoneInfo TimeZone = TimeZoneInfo.CreateCustomTimeZone(
        "Asia/Ho_Chi_Minh",
        TimeSpan.FromHours(7),
        "Vietnam Standard Time",
        "Vietnam Standard Time");

    public static DateOnly Today(TimeProvider clock) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), TimeZone).DateTime);

    /// <summary>Overload for the older <c>IDateTimeProvider.UtcNow</c> (a plain UTC DateTime).</summary>
    public static DateOnly Today(DateTime utcNow) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utcNow, TimeZone));

    /// <summary>The UTC instant a Vietnamese calendar day begins.</summary>
    public static DateTime StartOfDayUtc(DateOnly day) =>
        TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), TimeZone);

    /// <summary>
    /// WARD-12 mục 10 (Điều 61 Luật XLVPHC): the violator's "02 ngày làm việc" / "05 ngày làm
    /// việc" giải trình window, counted in working days (Mon-Fri; no public-holiday calendar is
    /// modelled here, so a holiday inside the window is not subtracted -- acceptable slack in the
    /// violator's favour, never against them).
    /// </summary>
    public static DateOnly AddWorkingDays(DateOnly from, int workingDays)
    {
        var day = from;
        var added = 0;
        while (added < workingDays)
        {
            day = day.AddDays(1);
            if (day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                added++;
            }
        }
        return day;
    }
}
