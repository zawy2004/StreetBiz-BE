using System.Security.Cryptography;
using System.Text;
using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.AiAssistance;

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
    /// <summary>60 s of 16 kHz mono WAV is ~1.9 MB; compressed clips are far smaller.</summary>
    public const int MaxAudioBytes = 4 * 1024 * 1024;
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
            .WithMessage("Đoạn ghi âm quá dài. Hãy nói ngắn hơn 60 giây.");

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

        RuleFor(x => x.Draft).SetValidator(new AssistDraftValidator());
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
    private readonly AssistTurnEngine engine = new(geocoding, sender);

    public async Task<AssistTurnResult> Handle(AssistRegistrationTurnCommand request, CancellationToken cancellationToken)
    {
        await vendorContext.RequireVendorIdAsync(cancellationToken);
        var userId = currentUser.UserId ?? throw new AuthenticationException(AppMessages.SessionExpired);
        var today = BusinessCalendar.Today(clock.UtcNow);
        var wards = await engine.WardsAsync(cancellationToken);

        var warnings = new List<string>();
        RegistrationExtraction? extraction = null;
        string? sttProvider = null;
        AssistHeard? heard = null;
        var startedAt = clock.UtcNow;

        if (request.Audio is not null || !string.IsNullOrWhiteSpace(request.Text))
        {
            var questionText = InterviewPlanner.Next(request.Draft, [], wards)?.Text ?? string.Empty;
            string? transcript;
            (transcript, extraction, sttProvider) = await HearAsync(request, request.Draft, questionText, today, cancellationToken);
            if (request.Audio is not null && string.IsNullOrWhiteSpace(transcript))
            {
                warnings.Add("Trợ lý chưa nghe thấy tiếng. Nói gần micro hơn, hoặc bấm \"Gõ thay\".");
            }

            heard = new AssistHeard(transcript, extraction?.Fields ?? [], extraction is not null);
        }

        var result = await engine.AdvanceAsync(request.QuestionKey, request.Draft, heard, today, wards, warnings, cancellationToken);

        if (extraction is not null)
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
                    transcriptSha256 = result.Transcript is null ? null : Sha256(result.Transcript),
                    proposals = result.Proposals.Select(p => new { p.Field, p.Value, p.Confidence, p.Source }),
                    warnings,
                    latencyMs = (int)(clock.UtcNow - startedAt).TotalMilliseconds,
                },
                result.Proposals.Count == 0 ? null : decimal.Round(result.Proposals.Average(p => p.Confidence) * 100, 2),
                cancellationToken);
        }

        return result;
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

    public static string Sha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}

/// <summary>The limits a client-held draft must respect, whichever mode sends it (turn or live).</summary>
public sealed class AssistDraftValidator : AbstractValidator<AssistDraft>
{
    public AssistDraftValidator()
    {
        RuleFor(x => x.Salutation)
            .Must(s => AssistSalutations.All.Contains(s))
            .WithMessage("Cách xưng hô không hợp lệ.");

        RuleFor(x => x.BusinessLine).MaximumLength(RegistrationFieldLimits.BusinessLine);
        RuleFor(x => x.DeclaredAddress).MaximumLength(RegistrationFieldLimits.DeclaredAddress);
        RuleFor(x => x.LocationText).MaximumLength(AssistGuards.MaxLocationText);
        RuleFor(x => x.VendorType)
            .Must(t => VendorTypes.All.Contains(t!)).When(x => x.VendorType is not null);
    }
}
