namespace StreetBiz.Application.Common.Security;

public static class PhoneMask
{
    /// <summary>"0901234567" becomes "090****567": enough to recognise a number without exposing it.</summary>
    public static string Mask(string phone) =>
        phone.Length <= 6
            ? new string('*', phone.Length)
            : string.Concat(phone.AsSpan(0, 3), new string('*', phone.Length - 6), phone.AsSpan(phone.Length - 3));
}
