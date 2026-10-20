using System.Text.RegularExpressions;

namespace StreetBiz.Application.Common.Security;

/// <summary>
/// Rules for REG-02 evidence uploads. A stored file is addressed as
/// <c>/api/uploads/evidence/{ownerUserId}/{32-hex}.{ext}</c>; that URL is what
/// RegistrationEvidence.file_url holds.
/// </summary>
public static partial class EvidenceFiles
{
    public const long MaxBytes = 5 * 1024 * 1024;
    public const string UrlPrefix = "/api/uploads/evidence/";

    /// <summary>
    /// BR-47 / PRI-06: how long an identity or licence document stays on file after
    /// upload. Stored on each row as retention_expires_at so disposal can be audited.
    /// </summary>
    public static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(365 * 2);

    public const string InvalidType = "Chỉ chấp nhận ảnh JPG, PNG, WEBP hoặc file PDF.";
    public const string TooLarge = "Dung lượng file tối đa 5 MB.";

    public static readonly IReadOnlyDictionary<string, string> ContentTypes = new Dictionary<string, string>
    {
        [".jpg"] = "image/jpeg",
        [".png"] = "image/png",
        [".webp"] = "image/webp",
        [".pdf"] = "application/pdf",
    };

    [GeneratedRegex(@"^[a-f0-9]{32}\.(jpg|png|webp|pdf)$")]
    private static partial Regex FileNameRegex();

    public static bool IsValidFileName(string fileName) => FileNameRegex().IsMatch(fileName);

    public static string NewFileName(string extension) => $"{Guid.NewGuid():N}{extension}";

    public static string BuildUrl(long ownerUserId, string fileName) => $"{UrlPrefix}{ownerUserId}/{fileName}";

    public static string StoragePath(long ownerUserId, string fileName) => $"evidence/{ownerUserId}/{fileName}";

    /// <summary>Parses a URL produced by <see cref="BuildUrl"/>; anything else is rejected.</summary>
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

    /// <summary>
    /// Identifies the file from its leading bytes. The client-supplied name and
    /// content type are not trusted, since either can be set to anything.
    /// </summary>
    public static string? DetectExtension(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
        {
            return ".jpg";
        }

        if (header.Length >= 8 && header[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            return ".png";
        }

        if (header.Length >= 4 && header[..4].SequenceEqual("%PDF"u8))
        {
            return ".pdf";
        }

        if (header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8))
        {
            return ".webp";
        }

        return null;
    }
}
