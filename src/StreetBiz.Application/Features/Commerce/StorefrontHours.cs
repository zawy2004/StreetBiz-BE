using StreetBiz.Application.Common.Exceptions;
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

/// <summary>One weekly opening window as the seller edits it: ISO weekday (Monday = 1) and "HH:mm" times.</summary>
public sealed record BusinessHourItem(short DayOfWeek, string OpensAt, string ClosesAt);

public sealed record ParsedBusinessHour(short DayOfWeek, TimeOnly OpensAt, TimeOnly ClosesAt);

/// <summary>
/// STORE-03: what a seller may publish as weekly hours. A window never crosses midnight (a night
/// shift is two rows: evening to 23:59, then 00:00 onwards on the next day) and every window has to
/// sit inside the hours the ward rents the pitch for.
/// </summary>
public static class StorefrontHoursRules
{
    public const int MaxPerDay = 3;
    public const int MaxTotal = 7 * MaxPerDay;

    private static readonly string[] DayNames =
        ["Thứ Hai", "Thứ Ba", "Thứ Tư", "Thứ Năm", "Thứ Sáu", "Thứ Bảy", "Chủ nhật"];

    public static string DayName(int dayOfWeek) => DayNames[dayOfWeek - 1];

    public static string Format(TimeOnly time) => time.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    public static bool TryParse(string? text, out TimeOnly time)
    {
        time = default;
        // Strict "HH:mm": TimeOnly.TryParse would also take "8", "8:5 PM" and culture-specific shapes.
        if (text is not { Length: 5 } || text[2] != ':')
        {
            return false;
        }

        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        if (!int.TryParse(text.AsSpan(0, 2), System.Globalization.NumberStyles.None, invariant, out var hour)
            || !int.TryParse(text.AsSpan(3, 2), System.Globalization.NumberStyles.None, invariant, out var minute)
            || hour > 23
            || minute > 59)
        {
            return false;
        }

        time = new TimeOnly(hour, minute);
        return true;
    }

    /// <summary>
    /// The window fits the ward's rental hours. No hours set on the zone means no limit; when the
    /// zone hours run overnight (from later than to) a window must sit in the evening part or the
    /// morning part.
    /// </summary>
    public static bool FitsRentalWindow(TimeOnly opens, TimeOnly closes, TimeOnly? from, TimeOnly? to)
    {
        if (from is null || to is null)
        {
            return true;
        }

        return from < to
            ? from <= opens && closes <= to
            : opens >= from || closes <= to;
    }

    public static string DescribeRentalWindow(TimeOnly from, TimeOnly to) =>
        from < to ? $"{Format(from)}–{Format(to)}" : $"{Format(from)}–{Format(to)} (qua đêm)";

    /// <summary>
    /// Returns the rows parsed, or throws a validation error keyed <c>hours[i]</c> for every row that
    /// breaks a rule, so the editor can point at the line.
    /// </summary>
    public static IReadOnlyList<ParsedBusinessHour> Validate(
        IReadOnlyList<BusinessHourItem>? items,
        TimeOnly? rentalFrom,
        TimeOnly? rentalTo)
    {
        items ??= [];
        var errors = new Dictionary<string, List<string>>();
        void Fail(int index, string message)
        {
            var key = $"hours[{index}]";
            if (!errors.TryGetValue(key, out var list))
            {
                errors[key] = list = [];
            }

            list.Add(message);
        }

        if (items.Count > MaxTotal)
        {
            throw new ValidationAppException(new Dictionary<string, string[]>
            {
                ["hours"] = [$"Tối đa {MaxPerDay} khung giờ mỗi ngày."],
            });
        }

        var parsed = new List<(int Index, ParsedBusinessHour Hour)>();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.DayOfWeek is < 1 or > 7)
            {
                Fail(i, "Ngày trong tuần phải từ 1 (Thứ Hai) đến 7 (Chủ nhật).");
                continue;
            }

            var day = DayName(item.DayOfWeek);
            if (!TryParse(item.OpensAt, out var opens) || !TryParse(item.ClosesAt, out var closes))
            {
                Fail(i, $"{day}: giờ phải có dạng HH:mm, ví dụ 08:30.");
                continue;
            }

            if (opens >= closes)
            {
                Fail(i, $"{day}: giờ mở cửa phải trước giờ đóng cửa. Mở đến nửa đêm thì nhập 23:59, ca sau nửa đêm tách thành dòng của ngày kế tiếp.");
                continue;
            }

            if (!FitsRentalWindow(opens, closes, rentalFrom, rentalTo))
            {
                Fail(i, $"{day}: {Format(opens)}–{Format(closes)} nằm ngoài khung giờ cho thuê của khu vực " +
                    $"({DescribeRentalWindow(rentalFrom!.Value, rentalTo!.Value)}).");
                continue;
            }

            parsed.Add((i, new ParsedBusinessHour(item.DayOfWeek, opens, closes)));
        }

        foreach (var day in parsed.GroupBy(row => row.Hour.DayOfWeek))
        {
            var ordered = day.OrderBy(row => row.Hour.OpensAt).ToArray();
            if (ordered.Length > MaxPerDay)
            {
                foreach (var extra in ordered.Skip(MaxPerDay))
                {
                    Fail(extra.Index, $"{DayName(day.Key)}: tối đa {MaxPerDay} khung giờ mỗi ngày.");
                }
            }

            // Touching windows (12:00 close, 12:00 open) are fine: opening is inclusive, closing exclusive.
            for (var n = 1; n < ordered.Length; n++)
            {
                if (ordered[n].Hour.OpensAt < ordered[n - 1].Hour.ClosesAt)
                {
                    Fail(ordered[n].Index, $"{DayName(day.Key)}: khung {Format(ordered[n].Hour.OpensAt)}–{Format(ordered[n].Hour.ClosesAt)} " +
                        $"chồng lên khung {Format(ordered[n - 1].Hour.OpensAt)}–{Format(ordered[n - 1].Hour.ClosesAt)}.");
                }
            }
        }

        if (errors.Count > 0)
        {
            throw new ValidationAppException(errors.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray()));
        }

        return parsed.Select(row => row.Hour).OrderBy(h => h.DayOfWeek).ThenBy(h => h.OpensAt).ToArray();
    }
}
