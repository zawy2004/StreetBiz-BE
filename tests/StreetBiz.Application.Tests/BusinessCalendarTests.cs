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

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
