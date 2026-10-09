using System.Text.Json;

namespace StreetBiz.Application.Features.Chatbot;

/// <summary>What the vision model reported for a food photo. Only names it saw are searched; nothing here is verified.</summary>
public sealed record ChatbotDishHint(string Query, IReadOnlyList<string> Alternatives, string Confidence, string? Cues);

/// <summary>Display-only interpretation; catalog matches remain separate from visual evidence.</summary>
public static class ChatbotFoodPhoto
{
    public const string CandidatesKind = "dish_match";
    public const string CuesLabel = "Dấu hiệu nhận thấy";

    public static bool NeedsComparison(string question)
    {
        var text = ChatbotKnowledge.Normalize(question);
        return text.Contains("so sanh") || text.Contains("doi chieu");
    }

    // A bounded broader search, not a synonym invented by the model.
    public static string? BroaderQuery(string query)
    {
        var text = ChatbotKnowledge.Normalize(query).Trim();
        if (text.Contains("khong") || text.Contains(" va ") || text.Contains(" o ") || text.Contains("gan ")) return null;
        foreach (var dish in new[] { "bánh mì", "bún chả", "bún bò", "phở", "xôi", "cơm tấm", "hủ tiếu", "bánh cuốn", "bánh xèo" })
        {
            var prefix = ChatbotKnowledge.Normalize(dish);
            if (text.StartsWith(prefix + " ", StringComparison.Ordinal)) return dish;
        }
        return null;
    }

    /// <summary>Arguments were already validated by ChatbotTools; alternatives equal to the main name are dropped.</summary>
    public static ChatbotDishHint Hint(string arguments)
    {
        var args = ChatbotJson.Read<JsonElement>(arguments);
        var query = Clean(args.GetProperty("query").GetString()!);
        var alternatives = args.TryGetProperty("alternatives", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(a => Clean(a.GetString()!))
                .Where(a => a.Length >= 2 && ChatbotKnowledge.Normalize(a) != ChatbotKnowledge.Normalize(query))
                .DistinctBy(ChatbotKnowledge.Normalize).Take(2).ToArray()
            : [];
        var confidence = args.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : "medium";
        var cues = args.TryGetProperty("cues", out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? ChatbotPrivacy.Text(v.GetString()!.Trim(), 200) : null;
        return new(query, alternatives, confidence, cues);
    }

    public static string ConfidenceLabel(string confidence) => confidence switch
    {
        "high" => "Khá chắc",
        "low" => "Chưa chắc",
        _ => "Có thể",
    };

    public static ChatbotEvidence Candidates(ChatbotDishHint hint, DateTimeOffset observedAt)
    {
        var fields = new List<ChatbotField> { new(hint.Query, ConfidenceLabel(hint.Confidence)) };
        // Alternatives are always weaker than the model's first choice.
        fields.AddRange(hint.Alternatives.Select(a => new ChatbotField(a, ConfidenceLabel(hint.Confidence == "high" ? "medium" : "low"))));
        if (hint.Cues is { } cues) fields.Add(new(CuesLabel, cues));
        var source = new ChatbotSource("image-analysis", "Nhận định AI từ ảnh bạn gửi — chưa được xác minh", "IMAGE_ANALYSIS", observedAt, null, null);
        return new("food.photo", "{}", source, [new(CandidatesKind, "Món trong ảnh có thể là", fields)], [], false);
    }

    public static string Answer(string query, bool found, string? broader, IReadOnlyList<string>? alternatives = null)
    {
        var label = Clean(query);
        var intro = $"Ảnh có thể là **{label}**. Đây là nhận định từ hình ảnh, chưa xác minh món hoặc thành phần.";
        if (alternatives is { Count: > 0 })
            intro += " Cũng có thể là " + string.Join(" hoặc ", alternatives.Select(a => $"**{Clean(a)}**")) + ".";
        var match = found
            ? broader is null
                ? "Đã tìm thấy thông tin món/quầy liên quan trong dữ liệu công khai. Chạm vào thẻ bên dưới để xem ảnh, giá và thông tin niêm yết."
                : $"Chưa tìm thấy kết quả cho tên món cụ thể; dưới đây là các quầy liên quan đến **{broader}**, không bảo đảm bán đúng biến thể trong ảnh."
            : "Chưa tìm thấy món/quầy phù hợp trong lần tra cứu này; điều đó không có nghĩa toàn bộ website không bán. Bạn có thể cho biết tên gọi khác hoặc chọn danh mục để tìm tiếp.";
        return intro + "\n\n" + match + "\n\nChưa đối chiếu ảnh thực tế của quầy, chưa xác nhận còn món hôm nay.";
    }

    private static readonly Lazy<(string Version, string[][] Groups)> Synonyms = new(() =>
    {
        using var stream = typeof(ChatbotFoodPhoto).Assembly.GetManifestResourceStream("StreetBiz.Chatbot.DishSynonyms.json")
            ?? throw new InvalidOperationException("Dish synonym list is missing from the build.");
        var root = JsonDocument.Parse(stream).RootElement;
        return (root.GetProperty("version").GetString()!,
            root.GetProperty("groups").EnumerateArray().Select(g => g.EnumerateArray().Select(n => n.GetString()!).ToArray()).ToArray());
    });

    public static string SynonymsVersion => Synonyms.Value.Version;

    /// <summary>Other curated names for exactly this dish; a query that only contains a name is not expanded.</summary>
    public static IReadOnlyList<string> OtherNames(string query)
    {
        var wanted = ChatbotKnowledge.Normalize(query).Trim();
        return Synonyms.Value.Groups
            .FirstOrDefault(g => g.Any(name => ChatbotKnowledge.Normalize(name) == wanted))?
            .Where(name => ChatbotKnowledge.Normalize(name) != wanted).ToArray() ?? [];
    }

    private static string Clean(string value) => new string(value.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-').ToArray()).Trim();
}
