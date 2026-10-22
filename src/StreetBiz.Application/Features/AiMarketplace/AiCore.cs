using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StreetBiz.Application.Features.AiMarketplace;

/// <summary>An image handed to the model: the bytes themselves, never a URL it would have to fetch.</summary>
public sealed record AiImage(byte[] Bytes, string MimeType);

/// <summary>What the model answered, or that it did not (no key, quota, timeout, an unreadable reply).</summary>
public sealed record AiGeneration(bool Success, string? Json);

/// <summary>
/// The one way the marketplace and report-assist features reach a generative model. It returns the
/// model's JSON text and nothing else, so every feature validates the answer against its own schema
/// before using any of it. A failure is a normal result, not an exception: the feature falls back
/// and the main flow carries on (BR-41, the model never blocks or decides anything).
/// </summary>
public interface IAiSuggestionClient
{
    Task<AiGeneration> GenerateJsonAsync(string prompt, IReadOnlyList<AiImage> images, CancellationToken cancellationToken);
}

/// <summary>The configuration switches for the AI features (Ai:Enabled and Ai:Features:AIP-01 …); on unless switched off.</summary>
public interface IAiFeatureGate
{
    bool IsEnabled(string featureCode);
}

/// <summary>The feature codes written to AIAssistanceLogs by the marketplace and report-assist features.</summary>
public static class AiMarketplaceFeatures
{
    public const string ReportAssist = "AIB-01";
    public const string DishDescription = "AIP-01";
    public const string DishRecognition = "AIP-02";
    public const string SearchIntent = "AIP-03";
    public const string ReviewSummary = "AIP-04";

    public static readonly IReadOnlyList<string> All =
        [ReportAssist, DishDescription, DishRecognition, SearchIntent, ReviewSummary];
}

public static class AiMarketplaceMessages
{
    public const string Disabled = "Tính năng gợi ý bằng AI đang tắt. Bạn vẫn có thể tự nhập.";
    public const string Unavailable = "Chưa tạo được gợi ý lúc này. Bạn vẫn có thể tự nhập.";
    public const string Advisory = "Gợi ý do AI tạo, chỉ để tham khảo.";
    public const string ImageInvalid = "Ảnh không hợp lệ hoặc không phải ảnh của bạn; hãy tải ảnh lên lại.";
}

/// <summary>
/// Turns model output into something safe to show or store. The model is asked for JSON, but its words
/// are still untrusted: they are stripped of markup, links and contact details, collapsed to plain
/// lines and capped, so even a prompt-injected reply cannot plant a link or a script in the product.
/// </summary>
public static partial class AiText
{
    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex Markup();

    [GeneratedRegex(@"(?i)\b(?:https?://|www\.)\S+")]
    private static partial Regex Link();

    [GeneratedRegex(@"[\w.+-]+@[\w-]+\.[\w.-]+")]
    private static partial Regex Email();

    // A run of 9+ digits that may contain spaces, dots or dashes: a phone or bank number.
    [GeneratedRegex(@"(?<!\d)(?:\+?84|0)[\d .-]{8,12}(?!\d)")]
    private static partial Regex Phone();

    [GeneratedRegex(@"[\p{Cc}\p{Cf}]")]
    private static partial Regex Invisible();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>Plain, single-spaced text of at most <paramref name="maxLength"/> characters, cut at a word.</summary>
    public static string Clean(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var cleaned = Markup().Replace(text, " ");
        cleaned = Link().Replace(cleaned, " ");
        cleaned = Email().Replace(cleaned, " ");
        cleaned = Phone().Replace(cleaned, " ");
        cleaned = Invisible().Replace(cleaned, " ");
        cleaned = Whitespace().Replace(cleaned, " ").Trim();
        return Truncate(cleaned, maxLength);
    }

    /// <summary>Review text goes INTO a prompt: angle brackets are replaced so it cannot close the tag it is wrapped in.</summary>
    public static string ForPrompt(string? text, int maxLength)
    {
        var plain = Whitespace().Replace(Invisible().Replace(text ?? string.Empty, " "), " ").Trim();
        return Truncate(plain.Replace('<', '‹').Replace('>', '›'), maxLength);
    }

    public static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        var cut = text[..maxLength];
        var space = cut.LastIndexOf(' ');
        return (space > maxLength / 2 ? cut[..space] : cut).TrimEnd(' ', ',', ';', ':', '-') + "…";
    }

    /// <summary>Hex SHA-256 of the pieces, for "has the input changed since the last answer" cache keys.</summary>
    public static string Fingerprint(params string[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001f', parts)))).ToLowerInvariant();
}

/// <summary>Reads a model's JSON reply defensively: a missing or wrongly typed field is simply absent.</summary>
public static class AiJson
{
    public static JsonElement? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            // The model is asked for bare JSON, but a fenced block ("```json … ```") is common enough to accept.
            var text = json.Trim();
            if (text.StartsWith("```", StringComparison.Ordinal))
            {
                var start = text.IndexOf('\n');
                var end = text.LastIndexOf("```", StringComparison.Ordinal);
                if (start >= 0 && end > start)
                {
                    text = text[(start + 1)..end].Trim();
                }
            }

            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string? Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static bool? Bool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : null;

    public static decimal? Num(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)
            ? d
            : null;

    public static IReadOnlyList<string> Strings(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToArray()
            : [];
}
