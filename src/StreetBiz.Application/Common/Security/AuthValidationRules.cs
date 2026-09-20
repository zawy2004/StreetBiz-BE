using System.Text.RegularExpressions;

namespace StreetBiz.Application.Common.Security;

public static partial class AuthValidationRules
{
    public const int PasswordMinLength = 8;

    // Vietnamese phone: local 0XXXXXXXXX (10 digits) or +84XXXXXXXXX.
    [GeneratedRegex(@"^(0\d{9}|\+84\d{9})$")]
    public static partial Regex PhoneRegex();

    // >= 8 chars, at least one upper, one lower, one digit, one special, no whitespace (BR-59).
    [GeneratedRegex(@"^(?=.*[a-z])(?=.*[A-Z])(?=.*\d)(?=.*[^\w\s])\S{8,}$")]
    public static partial Regex PasswordRegex();

    [GeneratedRegex(@"^\d{6}$")]
    public static partial Regex OtpRegex();

    /// <summary>
    /// CR-06 / BR-04: `+84905000001` and `0905000001` are the same subscriber, so both
    /// collapse to the local 0-prefixed form before a number is stored or looked up.
    /// Without this the unique index on phone_number would happily hold both as two
    /// separate accounts. Input that is not a recognised Vietnamese number is returned
    /// trimmed and unchanged — validation, not this method, decides whether to reject it.
    /// </summary>
    public static string NormalizePhone(string? phoneNumber)
    {
        var value = phoneNumber?.Trim() ?? string.Empty;
        return value.StartsWith("+84", StringComparison.Ordinal) && value.Length == 12
            ? string.Concat("0", value.AsSpan(3))
            : value;
    }
}
