using System.Globalization;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.AdministrativeUnits;

namespace StreetBiz.Application.Features.VendorRegistration.AssistTurn;

/// <summary>
/// Picks the next thing the assistant asks, from what the draft still lacks. The questions are
/// fixed text, not generated: predictable for a vendor who cannot read well, cheap, and nothing an
/// LLM could invent. A field asked twice without a usable answer falls back to typing (T7).
/// </summary>
public static class InterviewPlanner
{
    /// <summary>Asked this many times without a usable answer → the client shows a text box.</summary>
    public const int MaxVoiceAttempts = 2;

    public static AssistQuestion? Next(AssistDraft draft, IReadOnlyList<AssistWardOption> wardOptions, IReadOnlyList<WardDto> wards)
    {
        var s = draft.Salutation is { } sal && AssistSalutations.All.Contains(sal) ? sal : "bạn";
        var S = Capitalise(s);

        if (string.IsNullOrWhiteSpace(draft.BusinessLine))
        {
            return Attempts(draft, AssistQuestionKeys.Opening) == 0
                ? Voice(AssistQuestionKeys.Opening, $"{S} bán món gì, thường bán ở đâu, mấy giờ ạ?")
                : VoiceOrManual(draft, AssistQuestionKeys.BusinessLine, $"{S} bán món gì ạ?", AssistFields.BusinessLine, S);
        }

        if (string.IsNullOrWhiteSpace(draft.VendorType))
        {
            return new AssistQuestion(
                AssistQuestionKeys.VendorType,
                $"{S} bán bằng xe đẩy, gánh, hay có cửa hàng cố định?",
                AssistInputs.Choice,
                [
                    new AssistChoice(VendorTypes.Itinerant, "Xe đẩy, gánh, bán rong"),
                    new AssistChoice(VendorTypes.FixedStorefront, "Cửa hàng, ki-ốt cố định"),
                ],
                AssistFields.VendorType);
        }

        if (!draft.LocationConfirmed && !draft.LocationSkipped)
        {
            if (draft.Latitude is not null && draft.Longitude is not null)
            {
                return new AssistQuestion(
                    AssistQuestionKeys.Location,
                    $"Có phải {s} bán ở chỗ này không? Chạm vào bản đồ để dời ghim nếu chưa đúng.",
                    AssistInputs.Map,
                    [],
                    AssistFields.LocationText,
                    Skippable: true);
            }

            return Attempts(draft, AssistQuestionKeys.Location) >= MaxVoiceAttempts
                ? new AssistQuestion(
                    AssistQuestionKeys.Location,
                    $"Trợ lý chưa tìm ra chỗ đó. {S} chạm vào bản đồ để chọn chỗ bán nhé.",
                    AssistInputs.Map, [], AssistFields.LocationText, Skippable: true)
                : Voice(AssistQuestionKeys.Location,
                    $"{S} hay đứng bán gần đâu? Nói tên đường, chợ hoặc trường học gần đó.",
                    AssistFields.LocationText);
        }

        if (draft.WardUnitId is null)
        {
            var options = wardOptions.Count > 0
                ? wardOptions.Select(o => new AssistChoice(o.UnitId.ToString(CultureInfo.InvariantCulture), o.UnitName))
                : wards.Select(w => new AssistChoice(w.UnitId.ToString(CultureInfo.InvariantCulture), w.UnitName));
            return new AssistQuestion(
                AssistQuestionKeys.Ward,
                wardOptions.Count > 0 ? "Chỗ đó thuộc phường nào ạ? Trợ lý đoán là:" : "Chỗ đó thuộc phường nào ạ?",
                AssistInputs.Choice,
                options.ToList());
        }

        if (draft.VendorType == VendorTypes.FixedStorefront && string.IsNullOrWhiteSpace(draft.DeclaredAddress))
        {
            return VoiceOrManual(draft, AssistQuestionKeys.Address, "Địa chỉ cửa hàng là số mấy, đường nào ạ?", AssistFields.DeclaredAddress, S);
        }

        var numeric = NumericStep(draft, AssistQuestionKeys.Capital, AssistFields.CapitalAmount,
            draft.CapitalAmount is { } capital ? VietnameseSpokenValues.ReadMoney(capital) : null,
            "Vốn bỏ ra ban đầu khoảng bao nhiêu ạ?", S)
            ?? NumericStep(draft, AssistQuestionKeys.Labor, AssistFields.LaborCount,
                draft.LaborCount is { } labor ? VietnameseSpokenValues.ReadPeople(labor) : null,
                $"Có mấy người cùng bán, tính cả {s} ạ?", S)
            ?? NumericStep(draft, AssistQuestionKeys.StartDate, AssistFields.PlannedStartDate,
                draft.PlannedStartDate is { } start ? VietnameseSpokenValues.ReadDate(start) : null,
                $"{S} định bắt đầu bán từ ngày nào ạ?", S);
        if (numeric is not null)
        {
            return numeric;
        }

        if (draft.ContactSameAsPermanent is null)
        {
            return new AssistQuestion(
                AssistQuestionKeys.ContactAddress,
                $"{S} đang ở đúng địa chỉ thường trú ghi trên căn cước không ạ?",
                AssistInputs.Choice,
                [new AssistChoice("true", "Đúng, ở địa chỉ đó"), new AssistChoice("false", "Không, ở chỗ khác")],
                AssistFields.ContactSameAsPermanent);
        }

        // Optional, asked once: only helps match an itinerant cart to a slot it fits in.
        if (draft.VendorType == VendorTypes.Itinerant && draft.CartWidthMeters is null && !draft.CartWidthSkipped
            && Attempts(draft, AssistQuestionKeys.CartWidth) == 0)
        {
            return new AssistQuestion(
                AssistQuestionKeys.CartWidth,
                $"Xe hoặc gánh của {s} rộng chừng bao nhiêu mét? Không rõ thì bấm Bỏ qua.",
                AssistInputs.Voice, [], AssistFields.CartWidthMeters, Skippable: true);
        }

        return null;
    }

    private static AssistQuestion? NumericStep(
        AssistDraft draft, string key, string field, string? readBack, string question, string salutationCapitalised)
    {
        var confirmed = draft.Confirmed?.Contains(field, StringComparer.Ordinal) == true;
        if (readBack is null)
        {
            return VoiceOrManual(draft, key, question, field, salutationCapitalised);
        }

        return confirmed
            ? null
            : new AssistQuestion(
                "CONFIRM:" + field,
                readBack + ", đúng không ạ?",
                AssistInputs.Confirm,
                [new AssistChoice("yes", "Đúng rồi"), new AssistChoice("no", "Nói lại")],
                field);
    }

    private static AssistQuestion VoiceOrManual(AssistDraft draft, string key, string question, string field, string salutationCapitalised) =>
        Attempts(draft, key) >= MaxVoiceAttempts
            ? new AssistQuestion(key, $"Trợ lý chưa nghe rõ. {salutationCapitalised} gõ giúp vào ô bên dưới nhé.", AssistInputs.Manual, [], field)
            : Voice(key, question, field);

    private static AssistQuestion Voice(string key, string text, string? field = null) =>
        new(key, text, AssistInputs.Voice, [], field);

    private static int Attempts(AssistDraft draft, string key) =>
        draft.Attempts is not null && draft.Attempts.TryGetValue(key, out var n) ? n : 0;

    private static string Capitalise(string s) => s.Length == 0 ? s : char.ToUpper(s[0], CultureInfo.GetCultureInfo("vi-VN")) + s[1..];
}
