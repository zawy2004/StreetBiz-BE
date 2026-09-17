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
}
