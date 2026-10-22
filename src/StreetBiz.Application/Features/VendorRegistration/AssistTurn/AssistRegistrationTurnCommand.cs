using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.AdministrativeUnits;
using StreetBiz.Application.DTOs.Sidewalk;
using StreetBiz.Application.Features.AdministrativeUnits.ListWards;
using StreetBiz.Application.Features.AiAssistance;
using StreetBiz.Application.Features.SidewalkSlots.SearchSlots;

namespace StreetBiz.Application.Features.VendorRegistration.AssistTurn;

/// <summary>
/// X01 one interview turn: the vendor's answer (audio clip or typed text) to
/// <paramref name="QuestionKey"/>, plus the draft as the client holds it. With neither audio nor
/// text it only plans: returns the next question for the draft as it is (first call, or after the
/// vendor confirmed the map pin).
/// </summary>
public sealed record AssistRegistrationTurnCommand(
    string QuestionKey,
    AssistDraft Draft,
    string? Text,
    byte[]? Audio,
    string? AudioMimeType,
    bool VoiceConsent) : IRequest<AssistTurnResult>;

public sealed class AssistRegistrationTurnCommandValidator : AbstractValidator<AssistRegistrationTurnCommand>
{
    public const int MaxAudioBytes = 2 * 1024 * 1024;
    public const int MaxTextLength = 500;

    public static readonly IReadOnlySet<string> AudioMimeTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "audio/wav", "audio/x-wav", "audio/wave", "audio/mp4", "audio/m4a", "audio/x-m4a", "audio/aac",
        "audio/webm", "audio/ogg", "audio/mpeg", "audio/mp3",
    };

    public AssistRegistrationTurnCommandValidator()
    {
        RuleFor(x => x.QuestionKey)
            .Must(k => AssistQuestionKeys.All.Contains(k))
            .WithMessage("Câu hỏi không hợp lệ.");

        RuleFor(x => x)
            .Must(x => x.Audio is null || string.IsNullOrWhiteSpace(x.Text))
            .WithMessage("Chỉ gửi ghi âm hoặc chữ, không gửi cả hai.");

        RuleFor(x => x.Audio!.Length)
            .InclusiveBetween(1, MaxAudioBytes).When(x => x.Audio is not null)
            .WithMessage("Đoạn ghi âm quá dài. Hãy nói ngắn hơn 30 giây.");

        RuleFor(x => x.AudioMimeType)
            .Must(m => m is not null && AudioMimeTypes.Contains(BaseMime(m)))
            .When(x => x.Audio is not null)
            .WithMessage("Định dạng ghi âm không được hỗ trợ.");

        // Recording a voice is personal data processing in its own right (Luật BVDLCN 91/2025):
        // no clip is accepted without its own, explicit consent.
        RuleFor(x => x.VoiceConsent)
            .Equal(true).When(x => x.Audio is not null)
            .WithMessage("Cần đồng ý cho phép ghi âm trước khi nói.");

        RuleFor(x => x.Text).MaximumLength(MaxTextLength).WithMessage(RegMessages.FieldTooLong);

        RuleFor(x => x.Draft.Salutation)
            .Must(s => AssistSalutations.All.Contains(s))
            .WithMessage("Cách xưng hô không hợp lệ.");

        RuleFor(x => x.Draft.BusinessLine).MaximumLength(RegistrationFieldLimits.BusinessLine);
        RuleFor(x => x.Draft.DeclaredAddress).MaximumLength(RegistrationFieldLimits.DeclaredAddress);
        RuleFor(x => x.Draft.LocationText).MaximumLength(AssistGuards.MaxLocationText);
        RuleFor(x => x.Draft.VendorType)
            .Must(t => VendorTypes.All.Contains(t!)).When(x => x.Draft.VendorType is not null);
    }

    public static string BaseMime(string mime) => mime.Split(';')[0].Trim();
}

public sealed class AssistRegistrationTurnCommandHandler(
    IVendorContext vendorContext,
    ICurrentUser currentUser,
    IDateTimeProvider clock,
    ISpeechToText speechToText,
    IRegistrationExtractor extractor,
    IGeocodingService geocoding,
    ISender sender,
    IAiAssistanceLogs aiLogs) : IRequestHandler<AssistRegistrationTurnCommand, AssistTurnResult>
{
    /// <summary>Slots within this distance of the spoken spot are offered as a preview.</summary>
    public const double SlotPreviewRadiusMeters = 500;

    public async Task<AssistTurnResult> Handle(AssistRegistrationTurnCommand request, CancellationToken cancellationToken)
    {
        await vendorContext.RequireVendorIdAsync(cancellationToken);
        var userId = currentUser.UserId ?? throw new AuthenticationException(AppMessages.SessionExpired);
        var today = BusinessCalendar.Today(clock.UtcNow);
        var wards = await sender.Send(new ListWardsQuery(), cancellationToken);

        var draft = request.Draft;
        var warnings = new List<string>();
        string? transcript = null;
        RegistrationExtraction? extraction = null;
        string? sttProvider = null;
        var proposals = new List<AssistFieldProposal>();
        var answered = request.Audio is not null || !string.IsNullOrWhiteSpace(request.Text);
        var startedAt = clock.UtcNow;

        if (answered)
        {
            var questionText = InterviewPlanner.Next(draft, [], wards)?.Text ?? string.Empty;
            (transcript, extraction, sttProvider) = await HearAsync(request, draft, questionText, today, cancellationToken);
            if (request.Audio is not null && string.IsNullOrWhiteSpace(transcript))
            {
                warnings.Add("Trợ lý chưa nghe thấy tiếng. Nói gần micro hơn, hoặc bấm \"Gõ thay\".");
            }

            proposals = AssistGuards.KeepRelevant(
                AssistGuards.Accept(request.QuestionKey, transcript, extraction?.Fields ?? [], today, warnings),
                request.QuestionKey,
                draft);
            draft = Merge(draft, proposals, request.QuestionKey);
        }

        // Where the vendor sells: look the spoken place up, then which slots and wards are near it.
        AssistLocation? location = null;
        IReadOnlyList<AssistSlotPreview> slotPreview = [];
        var locationChanged = proposals.Any(p => p.Field == AssistFields.LocationText);
        if (locationChanged && !string.IsNullOrWhiteSpace(draft.LocationText))
        {
            var hits = await geocoding.SearchAsync($"{draft.LocationText}, Đà Nẵng", 3, cancellationToken);
            var hit = hits.FirstOrDefault();
            if (hit is null)
            {
                warnings.Add($"Chưa tìm thấy \"{draft.LocationText}\" trên bản đồ.");
                draft = draft with { Latitude = null, Longitude = null, LocationConfirmed = false };
            }
            else
            {
                draft = draft with { Latitude = hit.Latitude, Longitude = hit.Longitude, LocationConfirmed = false, WardUnitId = null };
                location = new AssistLocation(draft.LocationText!, hit.Latitude, hit.Longitude, hit.DisplayName ?? draft.LocationText!, []);
            }
        }

        IReadOnlyList<AssistWardOption> wardOptions = [];
        if (draft.Latitude is { } lat && draft.Longitude is { } lng)
        {
            var nearby = await sender.Send(
                new SearchSlotsQuery(lat, lng, SlotPreviewRadiusMeters, null, null, null, null, null, null, Take: 30),
                cancellationToken);
            slotPreview = nearby.Take(3).Select(s => Preview(s, draft)).ToList();

            // The ward question usually comes on a later turn (after the pin is confirmed), which
            // carries no fresh lookup: name the wards from the spoken place's label again. The
            // geocoder caches it, so this costs no second request.
            var mapLabel = location?.Label;
            if (mapLabel is null && draft.WardUnitId is null && !string.IsNullOrWhiteSpace(draft.LocationText))
            {
                mapLabel = (await geocoding.SearchAsync($"{draft.LocationText}, Đà Nẵng", 3, cancellationToken))
                    .FirstOrDefault()?.DisplayName;
            }

            wardOptions = WardOptions(nearby, mapLabel, wards);
            if (location is not null)
            {
                location = location with { WardOptions = wardOptions };
            }
        }

        if (answered && !proposals.Any(p => AssistGuards.FieldFor(request.QuestionKey) is { } f && p.Field == f)
            && request.QuestionKey != AssistQuestionKeys.Opening)
        {
            // Nothing usable for what was asked: count the try so the planner can switch to typing.
            draft = draft with { Attempts = Bump(draft.Attempts, request.QuestionKey) };
            if (!string.IsNullOrWhiteSpace(transcript))
            {
                warnings.Add("Trợ lý chưa nghe ra câu trả lời.");
            }
        }
        else if (answered && request.QuestionKey == AssistQuestionKeys.Opening)
        {
            draft = draft with { Attempts = Bump(draft.Attempts, AssistQuestionKeys.Opening) };
        }

        var next = InterviewPlanner.Next(draft, wardOptions, wards);

        if (answered && extraction is not null)
        {
            await aiLogs.RecordAsync(
                AiFeatureCodes.VoiceRegistration,
                AiLogEntities.UserAccount,
                userId,
                request.QuestionKey,
                new
                {
                    promptVersion = extraction.PromptVersion,
                    model = extraction.Model,
                    sttProvider,
                    input = request.Audio is not null ? "AUDIO" : "TEXT",
                    // The words themselves are not kept -- only proof of what was processed.
                    transcriptSha256 = transcript is null ? null : Sha256(transcript),
                    proposals = proposals.Select(p => new { p.Field, p.Value, p.Confidence, p.Source }),
                    warnings,
                    latencyMs = (int)(clock.UtcNow - startedAt).TotalMilliseconds,
                },
                proposals.Count == 0 ? null : decimal.Round(proposals.Average(p => p.Confidence) * 100, 2),
                cancellationToken);
        }

        return new AssistTurnResult(
            transcript,
            proposals,
            location,
            slotPreview,
            next,
            warnings,
            next is null,
            IsAiGenerated: extraction is not null,
            Draft: draft);
    }

    private async Task<(string? Transcript, RegistrationExtraction? Extraction, string? SttProvider)> HearAsync(
        AssistRegistrationTurnCommand request, AssistDraft draft, string questionText, DateOnly today, CancellationToken ct)
    {
        if (request.Audio is null)
        {
            var text = request.Text!.Trim();
            var fromText = await extractor.ExtractAsync(
                new RegistrationExtractionInput(request.QuestionKey, questionText, draft, today, text), ct);
            return (text, fromText, null);
        }

        var mime = AssistRegistrationTurnCommandValidator.BaseMime(request.AudioMimeType!);
        try
        {
            var heard = await speechToText.TranscribeAsync(request.Audio, mime, ct);
            if (string.IsNullOrWhiteSpace(heard.Text))
            {
                return (string.Empty, null, heard.Provider);
            }

            var fromSpeech = await extractor.ExtractAsync(
                new RegistrationExtractionInput(request.QuestionKey, questionText, draft, today, heard.Text), ct);
            return (heard.Text, fromSpeech, heard.Provider);
        }
        catch (AiUnavailableException) when (extractor.AcceptsAudio)
        {
            // Speech to text is down: let the extractor hear the clip itself (one hop).
            var oneHop = await extractor.ExtractAsync(
                new RegistrationExtractionInput(request.QuestionKey, questionText, draft, today, null, request.Audio, mime), ct);
            return (oneHop.Transcript, oneHop, "one-hop:" + oneHop.Model);
        }
    }

    /// <summary>Applies accepted values. A changed amount/date loses its earlier confirmation.</summary>
    public static AssistDraft Merge(AssistDraft draft, IReadOnlyList<AssistFieldProposal> proposals, string questionKey)
    {
        var confirmed = new HashSet<string>(draft.Confirmed ?? [], StringComparer.Ordinal);
        foreach (var p in proposals)
        {
            switch (p.Field)
            {
                case AssistFields.BusinessLine: draft = draft with { BusinessLine = p.Value }; break;
                case AssistFields.VendorType: draft = draft with { VendorType = p.Value }; break;
                case AssistFields.LocationText: draft = draft with { LocationText = p.Value }; break;
                case AssistFields.DeclaredAddress: draft = draft with { DeclaredAddress = p.Value }; break;
                case AssistFields.CapitalAmount:
                    var capital = decimal.Parse(p.Value, CultureInfo.InvariantCulture);
                    if (draft.CapitalAmount != capital) confirmed.Remove(p.Field);
                    draft = draft with { CapitalAmount = capital };
                    break;
                case AssistFields.LaborCount:
                    var labor = int.Parse(p.Value, CultureInfo.InvariantCulture);
                    if (draft.LaborCount != labor) confirmed.Remove(p.Field);
                    draft = draft with { LaborCount = labor };
                    break;
                case AssistFields.PlannedStartDate:
                    var start = DateOnly.ParseExact(p.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                    if (draft.PlannedStartDate != start) confirmed.Remove(p.Field);
                    draft = draft with { PlannedStartDate = start };
                    break;
                case AssistFields.ContactSameAsPermanent: draft = draft with { ContactSameAsPermanent = p.Value == "true" }; break;
                case AssistFields.Hours: draft = draft with { Hours = p.Value }; break;
                case AssistFields.CartWidthMeters:
                    draft = draft with { CartWidthMeters = decimal.Parse(p.Value, CultureInfo.InvariantCulture) };
                    break;
            }
        }

        return draft with { Confirmed = confirmed.ToList() };
    }

    private static AssistSlotPreview Preview(SidewalkSlotDto s, AssistDraft draft)
    {
        bool? fitsCart = draft.CartWidthMeters is { } cart && s.WidthMeters is { } width ? width >= cart : null;
        bool? fitsHours = null;
        if (AssistGuards.TryParseHours(draft.Hours, out var from, out var to) && s.AvailableFrom is { } open && s.AvailableTo is { } close)
        {
            fitsHours = from >= open && (to is null || to <= close);
        }

        return new AssistSlotPreview(
            s.SlotId, s.SlotCode, s.ZoneName, s.WardUnitId, Math.Round(s.DistanceMeters ?? 0),
            s.WidthMeters, s.LengthMeters, s.MonthlyPrice, s.AvailableFrom, s.AvailableTo, fitsCart, fitsHours);
    }

    /// <summary>
    /// Wards to offer, best guess first: the wards that own the nearby slots (a slot always
    /// belongs to exactly one ward), then wards whose name or district appears in the map label.
    /// The vendor still picks one -- this only orders the buttons.
    /// </summary>
    public static IReadOnlyList<AssistWardOption> WardOptions(
        IReadOnlyList<SidewalkSlotDto> nearbySlots, string? mapLabel, IReadOnlyList<WardDto> wards)
    {
        var byId = wards.ToDictionary(w => w.UnitId);
        var options = nearbySlots
            .GroupBy(s => s.WardUnitId)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Min(s => s.DistanceMeters ?? double.MaxValue))
            .Where(g => byId.ContainsKey(g.Key))
            .Select(g => new AssistWardOption(g.Key, byId[g.Key].UnitName, AssistWardReasons.SlotsNearby))
            .ToList();

        if (!string.IsNullOrWhiteSpace(mapLabel))
        {
            var label = AssistGuards.Fold(mapLabel);
            foreach (var w in wards)
            {
                if (options.Any(o => o.UnitId == w.UnitId))
                {
                    continue;
                }

                var name = AssistGuards.Fold(Regex.Replace(w.UnitName, @"^(phường|xã)\s+", string.Empty, RegexOptions.IgnoreCase));
                var district = w.ParentName is null ? null
                    : AssistGuards.Fold(Regex.Replace(w.ParentName, @"^(quận|huyện|thành phố)\s+", string.Empty, RegexOptions.IgnoreCase));
                if (label.Contains(name, StringComparison.Ordinal) || (district is not null && label.Contains(district, StringComparison.Ordinal)))
                {
                    options.Add(new AssistWardOption(w.UnitId, w.UnitName, AssistWardReasons.NameMatch));
                }
            }
        }

        return options.Take(4).ToList();
    }

    private static IReadOnlyDictionary<string, int> Bump(IReadOnlyDictionary<string, int>? attempts, string key)
    {
        var copy = attempts is null ? new Dictionary<string, int>(StringComparer.Ordinal) : new Dictionary<string, int>(attempts, StringComparer.Ordinal);
        copy[key] = copy.TryGetValue(key, out var n) ? n + 1 : 1;
        return copy;
    }

    private static string Sha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
