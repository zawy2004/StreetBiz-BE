namespace StreetBiz.Application.Common.Models;

/// <summary>
/// "Số tiền bằng chữ" for a payment receipt: 1_040_000 → "Một triệu không trăm bốn mươi nghìn
/// đồng". VND has no subunit, so only whole amounts are read. Follows the usual accounting
/// reading: a zero hundreds digit inside the number is read "không trăm", a zero tens digit
/// before a unit is "lẻ", and a group of three zeros is skipped entirely.
/// </summary>
public static class VietnameseMoneyWords
{
    private static readonly string[] Digits =
        ["không", "một", "hai", "ba", "bốn", "năm", "sáu", "bảy", "tám", "chín"];

    private static readonly string[] Scales = ["", "nghìn", "triệu", "tỷ", "nghìn tỷ", "triệu tỷ"];

    public static string Of(decimal amount)
    {
        if (amount < 0 || decimal.Truncate(amount) != amount)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "Only a whole, non-negative VND amount can be read.");
        }

        var value = (ulong)amount;
        if (value == 0)
        {
            return "Không đồng";
        }

        var groups = new List<int>();
        for (var rest = value; rest > 0; rest /= 1000)
        {
            groups.Add((int)(rest % 1000));
        }

        var words = new List<string>();
        for (var index = groups.Count - 1; index >= 0; index--)
        {
            if (groups[index] == 0)
            {
                continue;
            }

            var isLeading = index == groups.Count - 1;
            words.Add(ReadGroup(groups[index], isLeading));
            if (Scales[index].Length > 0)
            {
                words.Add(Scales[index]);
            }
        }

        var text = string.Join(' ', words) + " đồng";
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    private static string ReadGroup(int group, bool isLeading)
    {
        var hundreds = group / 100;
        var tens = group / 10 % 10;
        var units = group % 10;
        var parts = new List<string>();

        // Inside the number every group has three places, so a zero hundreds digit is still
        // read ("một triệu không trăm bốn mươi nghìn"); the leading group is read as written.
        if (hundreds > 0 || !isLeading)
        {
            parts.Add(Digits[hundreds]);
            parts.Add("trăm");
        }

        if (tens == 0)
        {
            if (units > 0 && parts.Count > 0)
            {
                parts.Add("lẻ");
            }
        }
        else if (tens == 1)
        {
            parts.Add("mười");
        }
        else
        {
            parts.Add(Digits[tens]);
            parts.Add("mươi");
        }

        if (units > 0)
        {
            parts.Add(units switch
            {
                1 when tens >= 2 => "mốt",
                4 when tens >= 2 => "tư",
                5 when tens >= 1 => "lăm",
                _ => Digits[units],
            });
        }

        return string.Join(' ', parts);
    }
}
