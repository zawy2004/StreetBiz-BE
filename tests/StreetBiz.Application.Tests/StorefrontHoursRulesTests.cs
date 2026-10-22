using FluentAssertions;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Features.Commerce;

namespace StreetBiz.Application.Tests;

/// <summary>STORE-03: what a seller may publish as weekly hours.</summary>
public sealed class StorefrontHoursRulesTests
{
    private static BusinessHourItem H(short day, string open, string close) => new(day, open, close);

    private static IDictionary<string, string[]> Errors(params BusinessHourItem[] items) =>
        FluentActions.Invoking(() => StorefrontHoursRules.Validate(items, null, null))
            .Should().Throw<ValidationAppException>().Which.Errors;

    [Theory]
    [InlineData("08:00", true)]
    [InlineData("23:59", true)]
    [InlineData("00:00", true)]
    [InlineData("24:00", false)]
    [InlineData("8:00", false)]
    [InlineData("08:60", false)]
    [InlineData("08-00", false)]
    [InlineData("8h", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Times_must_be_strict_HH_mm(string? text, bool valid) =>
        StorefrontHoursRules.TryParse(text, out _).Should().Be(valid);

    [Fact]
    public void A_valid_week_is_returned_ordered_by_day_then_opening_time()
    {
        var rows = StorefrontHoursRules.Validate(
            [H(3, "16:00", "20:00"), H(1, "13:00", "17:00"), H(1, "06:00", "11:00")], null, null);

        rows.Select(r => $"{r.DayOfWeek} {r.OpensAt:HH:mm}").Should().Equal("1 06:00", "1 13:00", "3 16:00");
    }

    [Fact]
    public void No_hours_at_all_is_valid_and_means_the_open_switch_alone_decides()
    {
        StorefrontHoursRules.Validate([], null, null).Should().BeEmpty();
        StorefrontHoursRules.Validate(null, null, null).Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    public void The_weekday_must_be_one_to_seven(short day) =>
        Errors(H(day, "08:00", "12:00")).Should().ContainKey("hours[0]");

    [Theory]
    [InlineData("12:00", "12:00")]
    [InlineData("18:00", "06:00")]
    public void Opening_must_be_before_closing_and_a_window_cannot_cross_midnight(string open, string close) =>
        Errors(H(1, open, close))["hours[0]"][0].Should().Contain("trước giờ đóng cửa");

    [Fact]
    public void Overlapping_windows_on_one_day_are_flagged_on_the_later_row()
    {
        var errors = Errors(H(1, "08:00", "12:00"), H(1, "11:59", "15:00"), H(2, "08:00", "12:00"));

        errors.Keys.Should().Equal("hours[1]");
        errors["hours[1]"][0].Should().Contain("chồng lên");
    }

    [Fact]
    public void Windows_that_only_touch_are_not_an_overlap()
    {
        StorefrontHoursRules.Validate([H(1, "08:00", "12:00"), H(1, "12:00", "15:00")], null, null)
            .Should().HaveCount(2);
    }

    [Fact]
    public void The_same_window_on_different_days_is_fine_but_a_fourth_window_in_one_day_is_not()
    {
        StorefrontHoursRules.Validate([H(1, "08:00", "12:00"), H(2, "08:00", "12:00")], null, null).Should().HaveCount(2);

        var errors = Errors(H(1, "06:00", "07:00"), H(1, "08:00", "09:00"), H(1, "10:00", "11:00"), H(1, "12:00", "13:00"));

        errors.Keys.Should().Equal("hours[3]");
    }

    [Fact]
    public void Several_bad_rows_are_all_reported_so_the_editor_can_mark_each_line()
    {
        var errors = Errors(H(1, "bad", "12:00"), H(2, "09:00", "08:00"), H(3, "08:00", "12:00"));

        errors.Keys.Should().BeEquivalentTo("hours[0]", "hours[1]");
    }

    [Theory]
    [InlineData("06:00", "22:00", "06:00", "22:00", true)]   // exactly the rental hours
    [InlineData("06:00", "22:00", "05:59", "12:00", false)]
    [InlineData("06:00", "22:00", "12:00", "22:01", false)]
    [InlineData("17:00", "22:00", "17:00", "22:00", true)]
    public void A_window_must_sit_inside_the_rental_hours(string from, string to, string open, string close, bool fits) =>
        StorefrontHoursRules.FitsRentalWindow(TimeOnly.Parse(open), TimeOnly.Parse(close), TimeOnly.Parse(from), TimeOnly.Parse(to))
            .Should().Be(fits);

    [Theory]
    [InlineData("18:00", "23:59", true)]
    [InlineData("00:00", "02:00", true)]
    [InlineData("01:00", "01:30", true)]
    [InlineData("10:00", "14:00", false)]
    [InlineData("17:00", "23:00", false)]
    public void Overnight_rental_hours_allow_the_evening_part_and_the_morning_part(string open, string close, bool fits) =>
        StorefrontHoursRules.FitsRentalWindow(TimeOnly.Parse(open), TimeOnly.Parse(close), new TimeOnly(18, 0), new TimeOnly(2, 0))
            .Should().Be(fits);

    [Fact]
    public void A_zone_without_hours_allows_any_window() =>
        StorefrontHoursRules.FitsRentalWindow(new TimeOnly(3, 0), new TimeOnly(4, 0), null, null).Should().BeTrue();

    [Fact]
    public void Open_now_treats_the_opening_minute_as_open_and_the_closing_minute_as_closed()
    {
        var hours = new[] { new StorefrontHourRow(1, new TimeOnly(8, 0), new TimeOnly(12, 0)) };

        StorefrontHours.IsOpen(hours, new MarketplaceOpenAt(1, new TimeOnly(8, 0))).Should().BeTrue();
        StorefrontHours.IsOpen(hours, new MarketplaceOpenAt(1, new TimeOnly(11, 59))).Should().BeTrue();
        StorefrontHours.IsOpen(hours, new MarketplaceOpenAt(1, new TimeOnly(12, 0))).Should().BeFalse();
        StorefrontHours.IsOpen(hours, new MarketplaceOpenAt(2, new TimeOnly(9, 0))).Should().BeFalse();
    }
}
