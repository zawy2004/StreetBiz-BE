using StreetBiz.Application.Common.Exceptions;

namespace StreetBiz.Application.Features.VendorRegistration.AssistTurn;

/// <summary>Speech to text for one short clip (≤ 60 s). The clip is never stored.</summary>
public interface ISpeechToText
{
    /// <exception cref="AiUnavailableException">Every configured provider failed.</exception>
    Task<SpeechTranscript> TranscribeAsync(byte[] audio, string mimeType, CancellationToken ct);
}

public sealed record SpeechTranscript(string Text, string Provider, int LatencyMs);

/// <summary>
/// Turns one answer into proposed draft values (LLM with a fixed JSON schema). Receives only the
/// non-sensitive <see cref="AssistDraft"/> -- never anything read from the CCCD.
/// </summary>
public interface IRegistrationExtractor
{
    /// <summary>True when this extractor can take the audio clip itself (one hop, used when
    /// speech-to-text is down).</summary>
    bool AcceptsAudio { get; }

    /// <exception cref="AiUnavailableException">Every configured model failed.</exception>
    Task<RegistrationExtraction> ExtractAsync(RegistrationExtractionInput input, CancellationToken ct);
}

/// <param name="Transcript">The answer as text (typed, or from speech to text). Null when
/// <paramref name="Audio"/> is given instead.</param>
public sealed record RegistrationExtractionInput(
    string QuestionKey,
    string QuestionText,
    AssistDraft Draft,
    DateOnly Today,
    string? Transcript,
    byte[]? Audio = null,
    string? AudioMimeType = null);

/// <param name="Value">Normalised to the draft format (digits, yyyy-MM-dd, "true"/"false", enum
/// code), or null when the model found nothing for the field.</param>
public sealed record ExtractedField(string Field, string? Value, decimal Confidence, string Evidence);

/// <param name="Transcript">Set when the extractor heard the audio itself.</param>
public sealed record RegistrationExtraction(
    string? Transcript,
    IReadOnlyList<ExtractedField> Fields,
    string Model,
    string PromptVersion,
    int LatencyMs);
