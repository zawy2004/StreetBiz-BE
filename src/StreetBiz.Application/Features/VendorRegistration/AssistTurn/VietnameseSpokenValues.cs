using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using StreetBiz.Application.Common.Models;

namespace StreetBiz.Application.Features.VendorRegistration.AssistTurn;

/// <summary>
/// Deterministic reading of the money amounts and dates a vendor says out loud ("ba chục triệu",
/// "5 triệu rưỡi", "thứ Hai tuần sau", "đầu tháng sau"). X01 uses it as the authority whenever it
/// recognises the phrase -- the LLM only fills in what this does not understand -- and to build
/// the spoken read-back the vendor confirms by tapping (numbers and dates are never accepted from
/// speech alone).
/// </summary>
public static partial class VietnameseSpokenValues
{
    private static readonly Dictionary<string, decimal> Units = new(StringComparer.Ordinal)
    {
        ["không"] = 0, ["một"] = 1, ["mốt"] = 1, ["hai"] = 2, ["ba"] = 3, ["bốn"] = 4, ["tư"] = 4,
        ["năm"] = 5, ["lăm"] = 5, ["nhăm"] = 5, ["sáu"] = 6, ["bảy"] = 7, ["bẩy"] = 7, ["tám"] = 8, ["chín"] = 9,
    };

    private static readonly Dictionary<string, decimal> Scales = new(StringComparer.Ordinal)
    {
        ["nghìn"] = 1_000m, ["triệu"] = 1_000_000m, ["tỷ"] = 1_000_000_000m,
    };

    /// <summary>
    /// The VND amount in a spoken phrase, or null when it holds no amount with a scale word
    /// (nghìn/triệu/tỷ) -- "vốn ba chục" is ambiguous and must be asked again, not guessed.
    /// When the phrase holds several amounts ("không phải ba trăm, ba mươi triệu thôi") the last
    /// one wins: that is how people correct themselves.
    /// </summary>
    public static decimal? ParseMoney(string? phrase)
    {
        var runs = NumberRuns(Normalise(phrase));
        for (var i = runs.Count - 1; i >= 0; i--)
        {
            var (value, largestScale) = Evaluate(runs[i]);
            // A scale word ("30 triệu"), or the amount written out in digits ("30.000.000").
            var spelledInDigits = runs[i].Count == 1 && DigitToken().IsMatch(runs[i][0]) && value >= 1_000m;
            if (value is > 0 && (largestScale >= 1_000m || spelledInDigits))
            {
                return decimal.Round(value.Value, 0, MidpointRounding.AwayFromZero);
            }
        }

        return null;
    }

    /// <summary>A small whole number ("hai", "mười lăm", "25") -- used for day and month numbers.</summary>
    public static int? ParseSmallNumber(string? phrase)
    {
        var runs = NumberRuns(Normalise(phrase));
        if (runs.Count == 0)
        {
            return null;
        }

        var (value, largestScale) = Evaluate(runs[^1]);
        return value is >= 0 and < 1_000 && largestScale < 1_000m && decimal.Truncate(value.Value) == value.Value
            ? (int)value.Value
            : null;
    }

    /// <summary>
    /// The calendar date a vendor means, relative to <paramref name="today"/> (Asia/Ho_Chi_Minh).
    /// Only unambiguous phrasings are read; "tuần sau", "sau Tết", "sắp tới" return null so the
    /// assistant asks for a day instead of picking one.
    /// </summary>
    public static DateOnly? ParseStartDate(string? phrase, DateOnly today)
    {
        var text = Normalise(phrase);
        if (text.Length == 0)
        {
            return null;
        }

        if (Regex.IsMatch(text, @"\b(hôm nay|bữa nay|ngay hôm nay)\b"))
        {
            return today;
        }

        if (Regex.IsMatch(text, @"\b(ngày mốt|mốt)\b") && !text.Contains("tháng"))
        {
            return today.AddDays(2);
        }

        if (Regex.IsMatch(text, @"\b(ngày mai|mai)\b") && !text.Contains("tháng"))
        {
            return today.AddDays(1);
        }

        var weekday = WeekdayIn(text);
        if (weekday is not null)
        {
            var mondayThisWeek = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
            var offset = ((int)weekday.Value + 6) % 7; // Monday = 0 ... Sunday = 6
            if (Regex.IsMatch(text, @"tuần (sau|tới|tới đây|kế)"))
            {
                return mondayThisWeek.AddDays(7 + offset);
            }

            var thisWeek = mondayThisWeek.AddDays(offset);
            if (text.Contains("tuần này"))
            {
                return thisWeek >= today ? thisWeek : null;
            }

            return thisWeek > today ? thisWeek : thisWeek.AddDays(7);
        }

        var firstOfThisMonth = new DateOnly(today.Year, today.Month, 1);
        if (Regex.IsMatch(text, @"(đầu|mùng một|mồng một) tháng (sau|tới)"))
        {
            return firstOfThisMonth.AddMonths(1);
        }

        if (Regex.IsMatch(text, @"giữa tháng (sau|tới)"))
        {
            return firstOfThisMonth.AddMonths(1).AddDays(14);
        }

        if (Regex.IsMatch(text, @"cuối tháng (này|nay)"))
        {
            return firstOfThisMonth.AddMonths(1).AddDays(-1);
        }

        // "15/11", "15-11-2026"
        var slash = Regex.Match(text, @"\b(\d{1,2})\s*[/\-.]\s*(\d{1,2})(?:\s*[/\-.]\s*(\d{4}))?\b");
        if (slash.Success)
        {
            return DateFrom(
                int.Parse(slash.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(slash.Groups[2].Value, CultureInfo.InvariantCulture),
                slash.Groups[3].Success ? int.Parse(slash.Groups[3].Value, CultureInfo.InvariantCulture) : null,
                today);
        }

        // "ngày 15 tháng 11 (năm 2026)", "mùng 5 tháng 12", "đầu tháng mười hai"
        var monthMatch = MonthPhrase().Match(text);
        if (monthMatch.Success)
        {
            var month = MonthNumber(monthMatch.Groups[1].Value);
            if (month is null)
            {
                return null;
            }

            int? year = monthMatch.Groups[2].Success ? int.Parse(monthMatch.Groups[2].Value, CultureInfo.InvariantCulture) : null;
            var before = text[..monthMatch.Index];
            if (Regex.IsMatch(before, @"\bđầu\s*$"))
            {
                return DateFrom(1, month.Value, year, today);
            }

            var day = Regex.Match(before, @"(?:ngày|mùng|mồng)\s+([\p{L}\d ]+?)\s*$");
            var dayNumber = day.Success ? ParseSmallNumber(day.Groups[1].Value) : null;
            return dayNumber is null ? null : DateFrom(dayNumber.Value, month.Value, year, today);
        }

        return null;
    }

    /// <summary>
    /// A cart or stall width in metres: "1m2", "1 mét 2", "một mét hai", "1,2 mét", "mét rưỡi",
    /// "tám tấc", "80 phân". Null outside 0.2–5 m (not a cart).
    /// </summary>
    public static decimal? ParseMeters(string? phrase)
    {
        var text = Normalise(phrase);
        decimal? meters = null;

        var compact = Regex.Match(text, @"\b(\d+)\s*m\s*(\d{1,2})\b");
        var withUnit = Regex.Match(text, @"(\S+(?:\s+\S+)?)\s+(?:mét|m)\b(?:\s+(rưỡi|\d{1,2}|một|hai|ba|bốn|tư|năm|sáu|bảy|tám|chín))?");
        var tac = Regex.Match(text, @"(\S+(?:\s+\S+)?)\s+tấc\b");
        var phan = Regex.Match(text, @"(\S+(?:\s+\S+)?)\s+(?:phân|cm|xăng ti mét|xen ti mét)\b");

        if (compact.Success)
        {
            var whole = int.Parse(compact.Groups[1].Value, CultureInfo.InvariantCulture);
            var frac = compact.Groups[2].Value;
            meters = whole + int.Parse(frac, CultureInfo.InvariantCulture) / (frac.Length == 1 ? 10m : 100m);
        }
        else if (withUnit.Success)
        {
            // "rộng chừng mét rưỡi" has no number before "mét": that is one metre.
            var before = withUnit.Groups[1].Value.Trim();
            var lastBefore = before.Split(' ')[^1];
            var whole = DigitToken().IsMatch(lastBefore) ? ParseDigits(lastBefore) : ParseSmallNumber(before) ?? 1m;

            var after = withUnit.Groups[2].Value;
            decimal frac = after switch
            {
                "" => 0,
                "rưỡi" => 0.5m,
                _ when DigitToken().IsMatch(after) => int.Parse(after, CultureInfo.InvariantCulture) / (after.Length == 1 ? 10m : 100m),
                _ => (ParseSmallNumber(after) ?? 0) / 10m,
            };
            meters = whole + frac;
        }
        else if (tac.Success)
        {
            meters = SmallOrDigits(tac.Groups[1].Value) / 10m;
        }
        else if (phan.Success)
        {
            meters = SmallOrDigits(phan.Groups[1].Value) / 100m;
        }

        return meters is >= 0.2m and <= 5m ? decimal.Round(meters.Value, 2) : null;
    }

    private static decimal? SmallOrDigits(string words)
    {
        var last = words.Split(' ')[^1];
        return DigitToken().IsMatch(last) ? ParseDigits(last) : ParseSmallNumber(words);
    }

    /// <summary>"Ba mươi triệu đồng" -- what the assistant reads back before the vendor taps Đúng.</summary>
    public static string ReadMoney(decimal amount) => VietnameseMoneyWords.Of(amount);

    /// <summary>"Ba người".</summary>
    public static string ReadPeople(int count)
    {
        var words = VietnameseMoneyWords.Of(count);
        return words[..^" đồng".Length] + " người";
    }

    /// <summary>"Thứ Hai, ngày 26 tháng 10 năm 2026".</summary>
    public static string ReadDate(DateOnly date)
    {
        var weekday = date.DayOfWeek switch
        {
            DayOfWeek.Monday => "Thứ Hai",
            DayOfWeek.Tuesday => "Thứ Ba",
            DayOfWeek.Wednesday => "Thứ Tư",
            DayOfWeek.Thursday => "Thứ Năm",
            DayOfWeek.Friday => "Thứ Sáu",
            DayOfWeek.Saturday => "Thứ Bảy",
            _ => "Chủ nhật",
        };
        return $"{weekday}, ngày {date.Day} tháng {date.Month} năm {date.Year}";
    }

    /// <summary>"1,2 mét".</summary>
    public static string ReadMeters(decimal meters) =>
        meters.ToString("0.##", CultureInfo.GetCultureInfo("vi-VN")) + " mét";

    // ---------------------------------------------------------------------------------------

    private static DateOnly? DateFrom(int day, int month, int? year, DateOnly today)
    {
        if (month is < 1 or > 12 || day < 1)
        {
            return null;
        }

        if (year is not null)
        {
            return day <= DateTime.DaysInMonth(year.Value, month) ? new DateOnly(year.Value, month, day) : null;
        }

        // No year: the next time that day comes round, today included.
        for (var y = today.Year; y <= today.Year + 1; y++)
        {
            if (day <= DateTime.DaysInMonth(y, month))
            {
                var candidate = new DateOnly(y, month, day);
                if (candidate >= today)
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static int? MonthNumber(string words)
    {
        var w = words.Trim();
        if (w.StartsWith("giêng", StringComparison.Ordinal))
        {
            return 1;
        }

        if (w.StartsWith("chạp", StringComparison.Ordinal))
        {
            return 12;
        }

        var n = ParseSmallNumber(w);
        return n is >= 1 and <= 12 ? n : null;
    }

    private static DayOfWeek? WeekdayIn(string text)
    {
        if (Regex.IsMatch(text, @"\bchủ nhật\b"))
        {
            return DayOfWeek.Sunday;
        }

        var m = Regex.Match(text, @"\bthứ\s+(hai|ba|tư|năm|sáu|bảy|bẩy|[2-7])\b");
        if (!m.Success)
        {
            return null;
        }

        return m.Groups[1].Value switch
        {
            "hai" or "2" => DayOfWeek.Monday,
            "ba" or "3" => DayOfWeek.Tuesday,
            "tư" or "4" => DayOfWeek.Wednesday,
            "năm" or "5" => DayOfWeek.Thursday,
            "sáu" or "6" => DayOfWeek.Friday,
            _ => DayOfWeek.Saturday,
        };
    }

    private static string Normalise(string? phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase))
        {
            return string.Empty;
        }

        var text = phrase.Normalize(NormalizationForm.FormC).ToLowerInvariant();
        text = MoneyShorthand().Replace(text, m => m.Groups[1].Value + " nghìn"); // "500k"
        text = text.Replace("ngàn", "nghìn").Replace("tỉ", "tỷ").Replace(" củ", " triệu")
            .Replace("đồng", " ").Replace("vnđ", " ").Replace("vnd", " ");
        // A comma separates two amounts ("không phải 300, 30 triệu"), so it stays as its own token
        // and breaks a number run -- but not inside "1,5" or "30,000".
        text = Regex.Replace(text, @",(?!\d)", " , ");
        text = Regex.Replace(text, @"[;!?""“”()]", " ");
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    private static readonly HashSet<string> NumberWords = new(StringComparer.Ordinal)
    {
        "mười", "mươi", "chục", "trăm", "lẻ", "linh", "rưỡi", "nghìn", "triệu", "tỷ",
    };

    private static bool IsNumberToken(string token) =>
        Units.ContainsKey(token) || NumberWords.Contains(token) || DigitToken().IsMatch(token);

    /// <summary>Maximal runs of number tokens, in order ("ba trăm" and "ba mươi triệu" are two runs).</summary>
    private static List<List<string>> NumberRuns(string text)
    {
        var runs = new List<List<string>>();
        List<string>? current = null;
        foreach (var raw in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.TrimEnd('.');
            if (IsNumberToken(token))
            {
                (current ??= []).Add(token);
            }
            else if (current is not null)
            {
                runs.Add(current);
                current = null;
            }
        }

        if (current is not null)
        {
            runs.Add(current);
        }

        // A lone "năm" next to "tháng"/"ngày" is a year/day word, not five -- runs made only of
        // ambiguous words without a scale are dropped by the callers' scale checks.
        return runs;
    }

    private static (decimal? Value, decimal LargestScale) Evaluate(List<string> tokens)
    {
        decimal total = 0, group = 0, small = 0, lastScale = 0, largestScale = 0;
        var any = false;
        foreach (var token in tokens)
        {
            if (DigitToken().IsMatch(token))
            {
                small += ParseDigits(token);
                any = true;
            }
            else if (Units.TryGetValue(token, out var unit))
            {
                small += unit;
                any = true;
            }
            else if (token == "mười")
            {
                small += 10;
                any = true;
            }
            else if (token is "mươi" or "chục")
            {
                small = (small == 0 ? 1 : small) * 10;
                any = true;
            }
            else if (token == "trăm")
            {
                group += (small == 0 ? 1 : small) * 100;
                small = 0;
                any = true;
            }
            else if (Scales.TryGetValue(token, out var scale))
            {
                var chunk = group + small;
                total += (chunk == 0 ? 1 : chunk) * scale;
                group = small = 0;
                lastScale = scale;
                largestScale = Math.Max(largestScale, scale);
                any = true;
            }
            else if (token == "rưỡi")
            {
                if (lastScale > 0 && group + small == 0)
                {
                    total += lastScale / 2;
                }
                else if (group > 0 && small == 0)
                {
                    group += 50; // "trăm rưỡi" = 150
                }
            }
            // "lẻ" / "linh" carry no value.
        }

        return any ? (total + group + small, largestScale) : (null, 0);
    }

    private static decimal ParseDigits(string token)
    {
        // "30.000.000" / "30,000,000" are thousands groupings; "5.5" / "5,5" are decimals.
        if (Regex.IsMatch(token, @"^\d{1,3}([.,]\d{3})+$"))
        {
            return decimal.Parse(token.Replace(".", string.Empty).Replace(",", string.Empty), CultureInfo.InvariantCulture);
        }

        return decimal.Parse(token.Replace(',', '.'), CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"^\d+(?:[.,]\d+)*$")]
    private static partial Regex DigitToken();

    [GeneratedRegex(@"(\d+(?:[.,]\d+)?)\s*k\b")]
    private static partial Regex MoneyShorthand();

    // Month 1–12 in digits or words; "năm 2026" after it is the year, not "five".
    [GeneratedRegex(@"tháng\s+(mười(?:\s+(?:một|hai))?|giêng|chạp|một|hai|ba|bốn|tư|năm|sáu|bảy|tám|chín|\d{1,2})(?:\s+năm\s+(\d{4}))?\b")]
    private static partial Regex MonthPhrase();
}
