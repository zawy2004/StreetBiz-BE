using FluentAssertions;
using StreetBiz.Application.Common.Models;

namespace StreetBiz.Application.Tests;

public sealed class BusinessCalendarTests
{
    [Fact]
    public void Today_rolls_over_at_midnight_in_Da_Nang_not_at_UTC_midnight()
    {
        // 01:00 on the 27th in Đà Nẵng is still the 26th in UTC.
        BusinessCalendar.Today(new FixedClock(new DateTimeOffset(2026, 9, 26, 18, 0, 0, TimeSpan.Zero)))
            .Should().Be(new DateOnly(2026, 9, 27));
    }

    [Fact]
    public void A_Vietnamese_day_starts_at_17_00_UTC_the_day_before()
    {
        BusinessCalendar.StartOfDayUtc(new DateOnly(2026, 10, 1))
            .Should().Be(new DateTime(2026, 9, 30, 17, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void A_day_ends_when_the_next_Vietnamese_day_starts()
    {
        BusinessCalendar.EndOfDayUtc(new DateOnly(2026, 10, 14))
            .Should().Be(new DateTime(2026, 10, 14, 17, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Working_days_are_counted_from_the_day_after_the_start_day()
    {
        // Monday 12/10/2026 + "02 ngày làm việc" = Tuesday and Wednesday.
        BusinessCalendar.AddWorkingDays(new DateOnly(2026, 10, 12), 2).Should().Be(new DateOnly(2026, 10, 14));
        // Thursday 15/10 + 5 working days skips the weekend: 16, 19, 20, 21, 22.
        BusinessCalendar.AddWorkingDays(new DateOnly(2026, 10, 15), 5).Should().Be(new DateOnly(2026, 10, 22));
    }

    [Fact]
    public void Tet_Nguyen_dan_2026_is_skipped()
    {
        // Friday 13/02/2026, then 16-20/02 are Tết (Thông báo 9441/TB-BNV): the next working day is 23/02.
        BusinessCalendar.AddWorkingDays(new DateOnly(2026, 2, 13), 1).Should().Be(new DateOnly(2026, 2, 23));
        BusinessCalendar.IsWorkingDay(new DateOnly(2026, 2, 18)).Should().BeFalse();
    }

    [Fact]
    public void A_swapped_weekday_is_off_and_its_make_up_Saturday_is_worked()
    {
        BusinessCalendar.IsWorkingDay(new DateOnly(2026, 8, 31)).Should().BeFalse(); // Monday, swapped off
        BusinessCalendar.IsWorkingDay(new DateOnly(2026, 8, 22)).Should().BeTrue();  // Saturday, làm bù
        BusinessCalendar.IsWorkingDay(new DateOnly(2026, 8, 29)).Should().BeFalse(); // ordinary Saturday
        // Friday 28/08 + 1 working day: 31/08, 01/09 and 02/09 are off, so Thursday 03/09.
        BusinessCalendar.AddWorkingDays(new DateOnly(2026, 8, 28), 1).Should().Be(new DateOnly(2026, 9, 3));
    }

    [Fact]
    public void A_holiday_on_a_Sunday_gives_the_next_Monday_off()
    {
        // Giỗ Tổ Hùng Vương 2026 is Sunday 26/04, so Monday 27/04 is off.
        BusinessCalendar.IsWorkingDay(new DateOnly(2026, 4, 27)).Should().BeFalse();
        BusinessCalendar.IsWorkingDay(new DateOnly(2026, 4, 28)).Should().BeTrue();
    }

    [Fact]
    public void Holidays_announced_after_release_come_from_configuration()
    {
        // A date no other test uses, since the calendar is process-wide.
        var announced = new DateOnly(2099, 11, 24);
        try
        {
            BusinessCalendar.IsWorkingDay(announced).Should().BeTrue();
            BusinessCalendar.Configure([announced], []);
            BusinessCalendar.IsWorkingDay(announced).Should().BeFalse();
            // Built-in holidays survive the configuration.
            BusinessCalendar.IsWorkingDay(new DateOnly(2026, 9, 2)).Should().BeFalse();
        }
        finally
        {
            BusinessCalendar.Configure([], []);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
