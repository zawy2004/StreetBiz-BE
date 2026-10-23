using System.Globalization;

namespace StreetBiz.Application.Common.Security;

/// <summary>
/// The text a permit QR carries (SIDE-08, BUY-02, WARD-11). A printed board encodes a link,
/// <c>{PublicScanBaseUrl}/q/{token}?v={version}</c>, so an ordinary phone camera opens the public
/// page; permits shown before that, and the paste boxes, carry the bare signed token. The link only
/// wraps the token <see cref="IPermitTokenService"/> already signed: nothing in it is sequential or
/// new, and validity is still decided by the signature and the live lookup, never by the link.
/// </summary>
public static class PermitQrText
{
    public const string ScanPath = "q";
    public const string PrintPath = "print";
    public const string VersionParameter = "v";

    /// <summary>
    /// The token inside scanned text: the segment after <c>/q/</c> of an http(s) link, otherwise the
    /// trimmed text itself. Any host is accepted (a board printed while the demo ran on another LAN
    /// address still scans); a link that names no token is returned as it is and fails verification.
    /// </summary>
    public static string ExtractToken(string? text)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        if (!TryHttpUri(trimmed, out var uri))
        {
            return trimmed;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = segments.Length - 2; i >= 0; i--)
        {
            if (segments[i] == ScanPath)
            {
                return Uri.UnescapeDataString(segments[i + 1]);
            }
        }

        return trimmed;
    }

    /// <summary>The board version printed on a scanned link (<c>?v=N</c>), when there is one.</summary>
    public static int? PrintedVersion(string? text)
    {
        if (!TryHttpUri(text?.Trim(), out var uri))
        {
            return null;
        }

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && parts[0] == VersionParameter
                && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var version)
                && version > 0)
            {
                return version;
            }
        }

        return null;
    }

    /// <summary>An absolute http(s) origin or path prefix with no trailing slash; null when unusable.</summary>
    public static string? NormalizeBaseUrl(string? configured)
    {
        if (!TryHttpUri(configured?.Trim(), out var uri) || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            return null;
        }

        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    /// <summary><c>{base}/q/{token}</c>, with <c>?v=N</c> for a printed board; null without a base URL.</summary>
    public static string? ScanUrl(string? baseUrl, string? token, int? version = null)
    {
        if (baseUrl is null || string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var url = $"{baseUrl}/{ScanPath}/{Uri.EscapeDataString(token.Trim())}";
        return version is > 0 ? $"{url}?{VersionParameter}={version.Value.ToString(CultureInfo.InvariantCulture)}" : url;
    }

    /// <summary><c>{base}/q/{token}/print</c>: the public A5 price-board page; null without a base URL.</summary>
    public static string? PrintUrl(string? baseUrl, string? token) =>
        ScanUrl(baseUrl, token) is { } url ? $"{url}/{PrintPath}" : null;

    private static bool TryHttpUri(string? text, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrEmpty(text) || !Uri.TryCreate(text, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        uri = parsed;
        return true;
    }
}

/// <summary>
/// Builds the public links for a permit token from <c>Permit:PublicScanBaseUrl</c>. Without that
/// setting every link is null and clients draw the bare token, as before.
/// </summary>
public interface IPermitLinks
{
    string? ScanUrl(string token, int? version = null);

    string? PrintUrl(string token);
}
