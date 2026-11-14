using System.Text.RegularExpressions;

namespace StreetBiz.Application.Common.Security;

/// <summary>
/// Profile photos. Public like dish photos (they appear in chat and on account lists), always
/// re-encoded by the server to a 256x256 JPEG, so every file is small and carries no EXIF/GPS:
/// <c>/api/uploads/avatars/{userId}/{32-hex}.jpg</c>.
/// </summary>
public static partial class AvatarFiles
{
    public const long MaxBytes = EvidenceFiles.MaxBytes;
    public const int SizePixels = 256;
    public const string UrlPrefix = "/api/uploads/avatars/";
    public const string Extension = ".jpg";
    public const string ContentType = "image/jpeg";

    [GeneratedRegex(@"^[a-f0-9]{32}\.jpg$")]
    private static partial Regex FileNameRegex();

    public static bool IsValidFileName(string fileName) => FileNameRegex().IsMatch(fileName);

    public static string BuildUrl(long userId, string fileName) => $"{UrlPrefix}{userId}/{fileName}";

    public static string StoragePath(long userId, string fileName) => $"avatars/{userId}/{fileName}";

    public static bool TryParseUrl(string? url, out long userId, out string fileName)
    {
        userId = 0;
        fileName = string.Empty;
        if (string.IsNullOrEmpty(url) || !url.StartsWith(UrlPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var parts = url[UrlPrefix.Length..].Split('/');
        if (parts.Length != 2 || !long.TryParse(parts[0], out userId) || !IsValidFileName(parts[1]))
        {
            return false;
        }

        fileName = parts[1];
        return true;
    }
}
