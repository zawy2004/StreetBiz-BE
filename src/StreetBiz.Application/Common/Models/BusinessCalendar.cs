using System.Collections.Frozen;

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

    // Replaced as a whole by Configure, never mutated, so readers need no lock.
    private static volatile WorkingDayCalendar calendar = WorkingDayCalendar.Create([], []);

    public static DateOnly Today(TimeProvider clock) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), TimeZone).DateTime);

    /// <summary>Overload for the older <c>IDateTimeProvider.UtcNow</c> (a plain UTC DateTime).</summary>
    public static DateOnly Today(DateTime utcNow) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utcNow, TimeZone));
    /// <summary>The Vietnamese calendar day a stored UTC instant falls on.</summary>
    public static DateOnly DateOf(DateTime utc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZone));

    /// <summary>The UTC instant a Vietnamese calendar day begins.</summary>
    public static DateTime StartOfDayUtc(DateOnly day) =>
        TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), TimeZone);

    /// <summary>
    /// The UTC instant a Vietnamese calendar day ends (the next day's start). A deadline "within N
    /// days" runs to the end of its last day (Điều 148 Bộ luật Dân sự 2015, applied to
    /// administrative violations by Điều 8 Luật XLVPHC), not to its first minute.
    /// </summary>
    public static DateTime EndOfDayUtc(DateOnly day) => StartOfDayUtc(day.AddDays(1));

    /// <summary>
    /// Adds the holidays and swapped working Saturdays announced after this build shipped (for
    /// example Ngày Văn hóa Việt Nam, or next year's Giỗ Tổ), on top of
    /// <see cref="VietnamPublicHolidays"/>. Called once at startup from configuration.
    /// </summary>
    public static void Configure(IEnumerable<DateOnly> extraHolidays, IEnumerable<DateOnly> extraWorkingDays) =>
        calendar = WorkingDayCalendar.Create(extraHolidays, extraWorkingDays);

    /// <summary>
    /// A state-sector working day in Việt Nam: Monday to Friday, minus public holidays and the
    /// weekdays swapped off around them, plus the Saturdays worked in exchange (làm bù).
    /// </summary>
    public static bool IsWorkingDay(DateOnly day) => calendar.IsWorkingDay(day);

    /// <summary>
    /// The day on which a period of <paramref name="workingDays"/> working days that starts after
    /// <paramref name="from"/> ends. The start day itself is not counted (Điều 147 Bộ luật Dân sự
    /// 2015), so "02 ngày làm việc" from a Monday ends on the Wednesday. WARD-12 mục 10 (Điều 61
    /// Luật XLVPHC): the violator's giải trình window. A holiday inside the window is skipped;
    /// a holiday missing from the calendar would shorten the window, against the violator.
    /// </summary>
    public static DateOnly AddWorkingDays(DateOnly from, int workingDays)
    {
        var current = calendar;
        var day = from;
        var added = 0;
        while (added < workingDays)
        {
            day = day.AddDays(1);
            if (current.IsWorkingDay(day))
            {
                added++;
            }
        }
        return day;
    }

    private sealed class WorkingDayCalendar
    {
        private readonly FrozenSet<DateOnly> holidays;
        private readonly FrozenSet<DateOnly> workingWeekends;

        private WorkingDayCalendar(FrozenSet<DateOnly> holidays, FrozenSet<DateOnly> workingWeekends)
        {
            this.holidays = holidays;
            this.workingWeekends = workingWeekends;
        }

        public static WorkingDayCalendar Create(IEnumerable<DateOnly> extraHolidays, IEnumerable<DateOnly> extraWorkingDays) =>
            new(
                VietnamPublicHolidays.DaysOff.Concat(extraHolidays).ToFrozenSet(),
                VietnamPublicHolidays.WorkingSaturdays.Concat(extraWorkingDays).ToFrozenSet());

        public bool IsWorkingDay(DateOnly day)
        {
            if (workingWeekends.Contains(day)) return true;
            if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;
            return !holidays.Contains(day);
        }
    }
}
