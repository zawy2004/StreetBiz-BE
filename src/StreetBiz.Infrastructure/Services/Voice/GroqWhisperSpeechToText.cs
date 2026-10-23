using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Features.VendorRegistration.AssistTurn;

namespace StreetBiz.Infrastructure.Services.Voice;

/// <summary>
/// X01 speech to text: Groq whisper-large-v3-turbo with Vietnamese forced and a vocabulary prompt
/// of dishes and Đà Nẵng places. Chosen at Gate 0 (22/10/2026): 93.6% of key words right on
/// clean and street-noise clips, ~0.4 s, and it accepts m4a (app) as well as wav/webm (web).
/// The clip is sent once and dropped; nothing is stored.
/// </summary>
public sealed class GroqWhisperSpeechToText(
    HttpClient httpClient,
    IConfiguration configuration,
    AiKeyPools keyPools,
    ILogger<GroqWhisperSpeechToText> logger) : ISpeechToText
{
    public const string DefaultModel = "whisper-large-v3-turbo";

    /// <summary>Biases recognition toward words vendors say: dishes, Đà Nẵng streets and markets.
    /// AiCompliance:VoiceRegistration:Vocabulary replaces it.</summary>
    public const string DefaultVocabulary =
        "bún bò, bún chả cá, bún mắm nêm, bánh mì, bánh mì que, bánh canh, bánh xèo, bánh bèo, bánh nậm, bánh tráng cuốn, " +
        "mì Quảng, cơm gà, chè, nước mía, cà phê muối, ốc hút, nem lụi, gỏi cuốn, bắp nướng, khoai nướng, " +
        "chợ Cồn, chợ Hàn, chợ Bắc Mỹ An, chợ đêm Sơn Trà, Hùng Vương, Lê Duẩn, Lê Lợi, Bạch Đằng, Hoàng Diệu, Trần Cao Vân, " +
        "Ông Ích Khiêm, Phan Châu Trinh, Phan Chu Trinh, Nguyễn Văn Linh, Tôn Đức Thắng, Nguyễn Văn Thoại, Mỹ Khê, Biển Đông, " +
        "cầu Rồng, sông Hàn, Hải Châu, Thanh Khê, Sơn Trà, Ngũ Hành Sơn, Liên Chiểu, Cẩm Lệ, kiệt, ki-ốt, xe đẩy, gánh, " +
        "triệu, nghìn, căn cước";

    /// <summary>A segment Whisper itself thinks is probably not speech is dropped.</summary>
    public const double NoSpeechThreshold = 0.6;

    /// <summary>
    /// What Whisper says when it hears silence or noise in Vietnamese: YouTube outros learnt from
    /// its training data ("Hãy subscribe cho kênh Ghiền Mì Gõ…"). Found on the emulator's silent
    /// microphone (23/10/2026). A segment containing one is dropped, so the vendor is asked again
    /// instead of reading a sentence they never said. Compared without accents.
    /// </summary>
    public static readonly IReadOnlyList<string> KnownHallucinations =
    [
        "subscribe", "ghien mi go", "dang ky kenh", "dang ky cho kenh", "nho dang ky", "like va share",
        "video hap dan", "cam on cac ban da theo doi", "cam on cac ban da xem", "hen gap lai cac ban",
    ];

    private static readonly IReadOnlyDictionary<string, string> Extensions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["audio/wav"] = "wav", ["audio/x-wav"] = "wav", ["audio/wave"] = "wav",
        ["audio/mp4"] = "m4a", ["audio/m4a"] = "m4a", ["audio/x-m4a"] = "m4a", ["audio/aac"] = "m4a",
        ["audio/webm"] = "webm", ["audio/ogg"] = "ogg", ["audio/mpeg"] = "mp3", ["audio/mp3"] = "mp3",
    };

    public async Task<SpeechTranscript> TranscribeAsync(byte[] audio, string mimeType, CancellationToken ct)
    {
        var keys = keyPools.Groq.GetAllKeysInOrder();
        if (keys.Length == 0)
        {
            logger.LogWarning("X01 speech to text: no Groq API keys configured in AiCompliance:Groq:ApiKeys.");
            throw Unavailable();
        }

        var model = configuration["AiCompliance:Groq:SttModel"]?.Trim() is { Length: > 0 } m ? m : DefaultModel;
        var vocabulary = configuration["AiCompliance:VoiceRegistration:Vocabulary"]?.Trim() is { Length: > 0 } v ? v : DefaultVocabulary;
        var extension = Extensions.TryGetValue(mimeType, out var ext) ? ext : "wav";
        var watch = Stopwatch.StartNew();

        foreach (var key in keys)
        {
            using var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(audio);
            file.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
            form.Add(file, "file", "clip." + extension);
            form.Add(new StringContent(model), "model");
            form.Add(new StringContent("vi"), "language");
            form.Add(new StringContent("0"), "temperature");
            // verbose_json: per-segment no_speech_prob, to drop what was never said.
            form.Add(new StringContent("verbose_json"), "response_format");
            form.Add(new StringContent(vocabulary), "prompt");

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.groq.com/openai/v1/audio/transcriptions") { Content = form };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            var suffix = key.Length > 4 ? key[^4..] : "****";
            try
            {
                using var response = await httpClient.SendAsync(request, ct);
                var body = await response.Content.ReadAsStringAsync(ct);
                if (response.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(body);
                    return new SpeechTranscript(SpokenText(doc.RootElement), "groq:" + model, (int)watch.ElapsedMilliseconds);
                }

                logger.LogWarning("X01 speech to text: Groq {Model} with key [..{Suffix}] failed with {Status}: {Body}",
                    model, suffix, (int)response.StatusCode, body.Length <= 300 ? body : body[..300]);
                if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnsupportedMediaType or HttpStatusCode.RequestEntityTooLarge)
                {
                    break; // the clip itself is the problem: another key will not help
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning("X01 speech to text: Groq key [..{Suffix}] timed out.", suffix);
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(ex, "X01 speech to text: Groq unreachable with key [..{Suffix}].", suffix);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "X01 speech to text: Groq answered with a body that is not JSON.");
            }
        }

        throw Unavailable();
    }

    /// <summary>The transcript without silence and without Whisper's known inventions.</summary>
    public static string SpokenText(JsonElement root)
    {
        var parts = new List<string>();
        if (root.TryGetProperty("segments", out var segments) && segments.ValueKind == JsonValueKind.Array && segments.GetArrayLength() > 0)
        {
            foreach (var segment in segments.EnumerateArray())
            {
                var silent = segment.TryGetProperty("no_speech_prob", out var p) && p.ValueKind == JsonValueKind.Number && p.GetDouble() >= NoSpeechThreshold;
                var text = segment.TryGetProperty("text", out var t) ? t.GetString()?.Trim() ?? string.Empty : string.Empty;
                if (!silent && text.Length > 0 && !IsHallucination(text))
                {
                    parts.Add(text);
                }
            }

            return string.Join(' ', parts);
        }

        var whole = root.TryGetProperty("text", out var w) ? w.GetString()?.Trim() ?? string.Empty : string.Empty;
        return IsHallucination(whole) ? string.Empty : whole;
    }

    private static bool IsHallucination(string text)
    {
        var folded = AssistGuards.Fold(text);
        return KnownHallucinations.Any(h => folded.Contains(h, StringComparison.Ordinal));
    }

    private static AiUnavailableException Unavailable() =>
        new("Trợ lý chưa nghe được lúc này. Bạn gõ giúp vào ô bên dưới nhé.");
}
