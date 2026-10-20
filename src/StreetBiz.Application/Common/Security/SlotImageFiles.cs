using System.Text.RegularExpressions;

namespace StreetBiz.Application.Common.Security;

/// <summary>
/// Photos of a sidewalk slot, uploaded by a ward officer. Vendors and guests browse slots
/// without any special access, so these are public and live under their own prefix:
/// <c>/api/uploads/slot-images/{ownerUserId}/{32-hex}.{ext}</c>.
/// </summary>
public static partial class SlotImageFiles
{
    public const long MaxBytes = EvidenceFiles.MaxBytes;
    public const int MaxPerSlot = 6;
    public const string UrlPrefix = "/api/uploads/slot-images/";
    public const string InvalidType = "Chỉ chấp nhận ảnh JPG, PNG hoặc WEBP.";
    public const string InvalidUrl = "Ảnh ô không hợp lệ; hãy tải ảnh lên lại.";
    public const string TooMany = "Mỗi ô tối đa 6 ảnh.";

    [GeneratedRegex(@"^[a-f0-9]{32}\.(jpg|png|webp)$")]
    private static partial Regex FileNameRegex();

    public static bool IsValidFileName(string fileName) => FileNameRegex().IsMatch(fileName);

    /// <summary>Photos only: the evidence sniffer also recognises PDF, which is refused here.</summary>
    public static string? DetectExtension(ReadOnlySpan<byte> header)
    {
        var extension = EvidenceFiles.DetectExtension(header);
        return extension is ".jpg" or ".png" or ".webp" ? extension : null;
    }

    public static string BuildUrl(long ownerUserId, string fileName) => $"{UrlPrefix}{ownerUserId}/{fileName}";

    public static string StoragePath(long ownerUserId, string fileName) => $"slots/{ownerUserId}/{fileName}";

    public static bool TryParseUrl(string? url, out long ownerUserId, out string fileName)
    {
        ownerUserId = 0;
        fileName = string.Empty;
        if (string.IsNullOrEmpty(url) || !url.StartsWith(UrlPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var parts = url[UrlPrefix.Length..].Split('/');
        if (parts.Length != 2 || !long.TryParse(parts[0], out ownerUserId) || !IsValidFileName(parts[1]))
        {
            return false;
        }

        fileName = parts[1];
        return true;
    }
}
