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

    /// <summary>The Vietnamese calendar day a stored UTC instant falls on.</summary>
    public static DateOnly DateOf(DateTime utc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZone));

    /// <summary>The UTC instant a Vietnamese calendar day begins.</summary>
    public static DateTime StartOfDayUtc(DateOnly day) =>
        TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), TimeZone);
}
