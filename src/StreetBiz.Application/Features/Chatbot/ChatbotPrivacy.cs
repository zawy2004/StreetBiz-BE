using System.Text.RegularExpressions;

namespace StreetBiz.Application.Features.Chatbot;

public static partial class ChatbotPrivacy
{
    [GeneratedRegex(@"(?i)(?:gsk_|sk-|AQ\.)[A-Za-z0-9_\-]{16,}|Bearer\s+\S+|eyJ[A-Za-z0-9_\-]{15,}\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+", RegexOptions.CultureInvariant)]
    private static partial Regex Credential();
    [GeneratedRegex(@"(?<!\d)\d[\d .\-]{8,20}\d(?!\d)")]
    private static partial Regex PersonalNumber();
    [GeneratedRegex(@"(?i)(otp|mật khẩu|mat khau|password|api[ _-]?key)\s*[:=]\s*\S+")]
    private static partial Regex LabeledSecret();
    [GeneratedRegex(@"[A-Za-z0-9.!#$%&'*+/=?^_`{|}~-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    public static string Text(string? value, int max = 800)
    {
        var text = value ?? "";
        text = Credential().Replace(text, "[đã ẩn thông tin xác thực]");
        text = LabeledSecret().Replace(text, "[đã ẩn thông tin xác thực]");
        text = PersonalNumber().Replace(text, "[đã ẩn số cá nhân]");
        text = Email().Replace(text, "[đã ẩn email]");
        return text.Length <= max ? text : text[..max] + "…";
    }

    public static void Validate(ChatbotSendRequest request, ChatbotSettings settings)
    {
        if (request.ResponseStyle is not (null or "concise" or "detailed" or "steps"))
            throw new ChatbotException(400, "invalid_response_style", "Chọn cách trả lời ngắn gọn, chi tiết hoặc từng bước.");
        if (!Guid.TryParse(request.ClientRequestId, out _) || string.IsNullOrWhiteSpace(request.Content)
            || request.Content.Length > settings.MaxQuestionCharacters)
            throw new ChatbotException(400, "invalid_message", $"Nhập câu hỏi từ 1 đến {settings.MaxQuestionCharacters} ký tự và mã yêu cầu hợp lệ.");
        if (Credential().IsMatch(request.Content) || LabeledSecret().IsMatch(request.Content))
            throw new ChatbotException(400, "sensitive_content", "Vui lòng bỏ mật khẩu, OTP hoặc khóa API khỏi câu hỏi.");
        if (request.AttachmentIds is { Length: > 2 })
            throw new ChatbotException(400, "attachment_limit", "Mỗi câu hỏi tối đa 2 ảnh.");
        if (request.AttachmentIds?.Any(id => !Guid.TryParse(id, out _)) == true)
            throw new ChatbotException(400, "invalid_attachment", "Mã ảnh không hợp lệ.");
        if (request.PageContext is { } context && (string.IsNullOrWhiteSpace(context.PageKey) || context.PageKey.Length > 60 || context.EntityId?.Length > 32))
            throw new ChatbotException(400, "invalid_context", "Ngữ cảnh trang không hợp lệ.");
        // Only the server's voice relay labels a turn as spoken.
        if (request.Channel is not null)
            throw new ChatbotException(400, "invalid_message", "Yêu cầu không hợp lệ.");
        if (request.Location is { } at && (at.Latitude is < -90 or > 90 || at.Longitude is < -180 or > 180))
            throw new ChatbotException(400, "invalid_location", "Vị trí không hợp lệ.");
    }

    /// <summary>About 100 m precision: enough to rank nearby stalls, without a precise trace of the user.</summary>
    public static ChatbotLocation? Coarse(ChatbotLocation? location) => location is null ? null
        : new(Math.Round(location.Latitude, 3, MidpointRounding.AwayFromZero), Math.Round(location.Longitude, 3, MidpointRounding.AwayFromZero));
}
