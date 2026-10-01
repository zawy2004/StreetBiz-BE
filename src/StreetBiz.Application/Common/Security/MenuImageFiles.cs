using System.Text.RegularExpressions;

namespace StreetBiz.Application.Common.Security;

/// <summary>
/// Dish photos. Unlike evidence files these are public (guests browse the menu), so
/// they live under their own prefix and are served without a login:
/// <c>/api/uploads/menu-images/{ownerUserId}/{32-hex}.{ext}</c>.
/// </summary>
public static partial class MenuImageFiles
{
    public const long MaxBytes = EvidenceFiles.MaxBytes;
    public const string UrlPrefix = "/api/uploads/menu-images/";
    public const string InvalidType = "Chỉ chấp nhận ảnh JPG, PNG hoặc WEBP.";
    public const string Required = "Món mới cần có ảnh món ăn.";
    public const string InvalidUrl = "Ảnh món không hợp lệ; tải ảnh lên lại.";

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

    public static string StoragePath(long ownerUserId, string fileName) => $"menu/{ownerUserId}/{fileName}";

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
