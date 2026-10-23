using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using StreetBiz.Application.Features.VendorRegistration.AssistTurn;

namespace StreetBiz.Infrastructure.Services.Voice;

/// <summary>
/// The one prompt and schema every X01 extractor uses (Gemini and Groq), so the two cannot drift.
/// Bump <see cref="Version"/> whenever the wording changes: it is written to AIAssistanceLogs with
/// every turn. Measured at Gate 0 (docs_system/features/x01-eval).
/// </summary>
public static class RegistrationExtractionPrompt
{
    public const string Version = "x01.v1";

    public static readonly IReadOnlyList<string> Fields =
    [
        AssistFields.BusinessLine, AssistFields.VendorType, AssistFields.LocationText, AssistFields.DeclaredAddress,
        AssistFields.CapitalAmount, AssistFields.LaborCount, AssistFields.PlannedStartDate, AssistFields.Hours,
        AssistFields.CartWidthMeters, AssistFields.ContactSameAsPermanent,
    ];

    private static readonly IReadOnlyDictionary<string, string> ValueTypes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [AssistFields.BusinessLine] = "string",
        [AssistFields.VendorType] = "string",
        [AssistFields.LocationText] = "string",
        [AssistFields.DeclaredAddress] = "string",
        [AssistFields.CapitalAmount] = "integer",
        [AssistFields.LaborCount] = "integer",
        [AssistFields.PlannedStartDate] = "string",
        [AssistFields.Hours] = "string",
        [AssistFields.CartWidthMeters] = "number",
        [AssistFields.ContactSameAsPermanent] = "boolean",
    };

    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");

    // Vietnamese stays readable in the prompt ("Bún bò", not "Bún bò").
    private static readonly JsonSerializerOptions DraftJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The system instruction. Only the non-sensitive draft goes in -- never CCCD data.</summary>
    public static string System(RegistrationExtractionInput input)
    {
        var draft = new
        {
            input.Draft.BusinessLine,
            input.Draft.VendorType,
            input.Draft.LocationText,
            input.Draft.DeclaredAddress,
            input.Draft.CapitalAmount,
            input.Draft.LaborCount,
            PlannedStartDate = input.Draft.PlannedStartDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            input.Draft.Hours,
            input.Draft.CartWidthMeters,
        };
        var source = input.Audio is null
            ? "Đầu vào là lời người bán đã được nhận dạng giọng nói thành chữ (có thể sai chính tả)."
            : "Đầu vào là đoạn ghi âm lời người bán. Chép lại nguyên văn lời nói vào transcript; evidence là đoạn chép nguyên văn trong transcript.";
        var weekday = Vi.DateTimeFormat.GetDayName(input.Today.DayOfWeek);

        return $$"""
            Bạn là bộ trích xuất dữ liệu cho hồ sơ đăng ký hộ kinh doanh nhỏ (bán hàng ăn uống) tại Đà Nẵng.
            Hôm nay là {{weekday}}, ngày {{input.Today.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}} (giờ Việt Nam).
            Câu trợ lý vừa hỏi (mã {{input.QuestionKey}}): "{{input.QuestionText}}"
            Bản nháp hiện có: {{JsonSerializer.Serialize(draft, DraftJson)}}
            {{source}}
            Trả về JSON đúng schema. Mỗi trường là {value, confidence (0..1), evidence}. Luật:
            1. Chỉ điền trường mà người bán NÓI RA trong lời này. Không suy đoán, không bịa. Không chắc thì value = null, evidence = "".
            2. evidence là đoạn chép NGUYÊN VĂN từ lời người bán (giữ đúng chữ, kể cả lỗi chính tả) chứa thông tin của trường.
            3. capitalAmount: số nguyên VND. "ba chục triệu" = 30000000; "chục triệu" = 10000000; "năm triệu rưỡi" = 5500000.
            4. laborCount: tổng số người cùng bán, tính cả người nói ("vợ chồng tui" = 2; "tui với hai đứa con" = 3; "một mình" = 1).
            5. plannedStartDate: YYYY-MM-DD. "mai" = ngày mai; "thứ Hai tuần sau" = thứ Hai của tuần kế tiếp; "đầu tháng sau" = ngày 1 của tháng sau; ngày không có năm thì lấy lần gần nhất trong tương lai. Mơ hồ ("sau Tết", "sắp tới") thì null.
            6. vendorType: "ITINERANT" nếu bán bằng xe đẩy, gánh, bán rong, đi dọc đường; "FIXED_STOREFRONT" nếu có cửa hàng, ki-ốt, mặt bằng cố định. Không nói thì null.
            7. Khi người bán tự sửa ("không phải X, Y thôi"; "hai, à không, ba") thì lấy giá trị sau cùng.
            8. locationText: tên chỗ bán để tìm trên bản đồ (chợ, đường, ngã tư, trường, công viên). Được sửa lỗi nhận dạng rõ ràng của địa danh Đà Nẵng (vd "chợ cồm" -> "chợ Cồn") nhưng evidence vẫn chép nguyên văn.
            9. declaredAddress: chỉ khi có số nhà/tên đường của cửa hàng cố định.
            10. businessLine: các món/mặt hàng, viết có dấu, ngăn cách bằng dấu phẩy (vd "Bún bò, bánh mì").
            11. hours: "HH:mm-HH:mm" giờ 24h; thiếu giờ kết thúc thì "HH:mm-". "sáng 6 giờ tới 10 giờ" = "06:00-10:00"; "chiều 3 giờ tới 7 giờ" = "15:00-19:00"; "tối từ 6 giờ" = "18:00-".
            12. cartWidthMeters: mét, số thập phân ("một mét hai" = 1.2).
            13. contactSameAsPermanent: chỉ điền khi câu hỏi là CONTACT_ADDRESS; true nếu đồng ý ở đúng địa chỉ trên căn cước, false nếu ở chỗ khác.
            """;
    }

    /// <summary>JSON Schema for OpenAI-compatible strict structured output (Groq).</summary>
    public static JsonObject JsonSchema(bool withTranscript)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        if (withTranscript)
        {
            properties["transcript"] = new JsonObject { ["type"] = "string" };
            required.Add("transcript");
        }

        foreach (var field in Fields)
        {
            properties[field] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["value"] = new JsonObject { ["type"] = new JsonArray(ValueTypes[field], "null") },
                    ["confidence"] = new JsonObject { ["type"] = "number" },
                    ["evidence"] = new JsonObject { ["type"] = "string" },
                },
                ["required"] = new JsonArray("value", "confidence", "evidence"),
                ["additionalProperties"] = false,
            };
            required.Add(field);
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
            ["additionalProperties"] = false,
        };
    }

    /// <summary>The same schema in Gemini's OpenAPI subset.</summary>
    public static JsonObject GeminiSchema(bool withTranscript)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        if (withTranscript)
        {
            properties["transcript"] = new JsonObject { ["type"] = "STRING" };
            required.Add("transcript");
        }

        foreach (var field in Fields)
        {
            var type = ValueTypes[field] switch { "integer" => "INTEGER", "number" => "NUMBER", "boolean" => "BOOLEAN", _ => "STRING" };
            properties[field] = new JsonObject
            {
                ["type"] = "OBJECT",
                ["properties"] = new JsonObject
                {
                    ["value"] = new JsonObject { ["type"] = type, ["nullable"] = true },
                    ["confidence"] = new JsonObject { ["type"] = "NUMBER" },
                    ["evidence"] = new JsonObject { ["type"] = "STRING" },
                },
                ["required"] = new JsonArray("value", "confidence", "evidence"),
            };
            required.Add(field);
        }

        return new JsonObject { ["type"] = "OBJECT", ["properties"] = properties, ["required"] = required };
    }

    /// <summary>Reads a model answer into fields; values are normalised to the draft's string format.</summary>
    public static (string? Transcript, IReadOnlyList<ExtractedField> Fields) Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string? transcript = root.TryGetProperty("transcript", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        var fields = new List<ExtractedField>();
        foreach (var field in Fields)
        {
            if (!root.TryGetProperty(field, out var f) || f.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var value = f.TryGetProperty("value", out var v) ? Normalise(v) : null;
            var confidence = f.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDecimal() : 0m;
            var evidence = f.TryGetProperty("evidence", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? string.Empty : string.Empty;
            fields.Add(new ExtractedField(field, value, Math.Clamp(confidence, 0m, 1m), evidence));
        }

        return (transcript, fields);
    }

    private static string? Normalise(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => string.IsNullOrWhiteSpace(v.GetString()) ? null : v.GetString(),
        JsonValueKind.Number => v.GetDecimal().ToString(CultureInfo.InvariantCulture),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null,
    };
}
