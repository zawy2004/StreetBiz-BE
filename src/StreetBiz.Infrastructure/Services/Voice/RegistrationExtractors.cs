using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Features.VendorRegistration.AssistTurn;

namespace StreetBiz.Infrastructure.Services.Voice;

/// <summary>X01 extraction with Gemini (text, or the audio clip itself in one hop).</summary>
public sealed class GeminiRegistrationExtractor(GeminiVisionClient gemini, IConfiguration configuration)
{
    public static readonly string[] DefaultModels = ["gemini-3.5-flash-lite", "gemini-3.8-flash"];

    public async Task<RegistrationExtraction?> ExtractAsync(RegistrationExtractionInput input, CancellationToken ct)
    {
        var models = configuration.GetSection("AiCompliance:VoiceRegistration:GeminiModels").Get<string[]>() is { Length: > 0 } configured
            ? configured
            : DefaultModels;
        var withAudio = input.Audio is not null;
        var watch = Stopwatch.StartNew();
        var (ok, content) = await gemini.CallStructuredAsync(
            RegistrationExtractionPrompt.System(input),
            withAudio ? null : input.Transcript,
            withAudio ? (input.Audio!, input.AudioMimeType ?? "audio/wav") : null,
            RegistrationExtractionPrompt.GeminiSchema(withTranscript: withAudio),
            models,
            ct);
        if (!ok || content is null)
        {
            return null;
        }

        try
        {
            var (transcript, fields) = RegistrationExtractionPrompt.Parse(content);
            return new RegistrationExtraction(
                withAudio ? transcript : input.Transcript, fields, "gemini", RegistrationExtractionPrompt.Version, (int)watch.ElapsedMilliseconds);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>X01 extraction with a Groq-hosted model (text only), strict JSON schema.</summary>
public sealed class GroqRegistrationExtractor(
    HttpClient httpClient,
    IConfiguration configuration,
    AiKeyPools keyPools,
    ILogger<GroqRegistrationExtractor> logger)
{
    public const string DefaultModel = "openai/gpt-oss-120b";

    public async Task<RegistrationExtraction?> ExtractAsync(RegistrationExtractionInput input, CancellationToken ct)
    {
        if (input.Transcript is null)
        {
            return null;
        }

        var model = configuration["AiCompliance:VoiceRegistration:GroqModel"]?.Trim() is { Length: > 0 } m ? m : DefaultModel;
        var payload = new JsonObject
        {
            ["model"] = model,
            ["temperature"] = 0,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = RegistrationExtractionPrompt.System(input) },
                new JsonObject { ["role"] = "user", ["content"] = input.Transcript }),
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = "registration_proposal",
                    ["strict"] = true,
                    ["schema"] = RegistrationExtractionPrompt.JsonSchema(withTranscript: false),
                },
            },
        }.ToJsonString();

        var watch = Stopwatch.StartNew();
        foreach (var key in keyPools.Groq.GetAllKeysInOrder())
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.groq.com/openai/v1/chat/completions")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            var suffix = key.Length > 4 ? key[^4..] : "****";
            try
            {
                using var response = await httpClient.SendAsync(request, ct);
                var body = await response.Content.ReadAsStringAsync(ct);
                if (response.IsSuccessStatusCode)
                {
                    var content = JsonNode.Parse(body)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(content))
                    {
                        return null;
                    }

                    var (_, fields) = RegistrationExtractionPrompt.Parse(content);
                    return new RegistrationExtraction(input.Transcript, fields, "groq:" + model, RegistrationExtractionPrompt.Version, (int)watch.ElapsedMilliseconds);
                }

                logger.LogWarning("X01 extraction: Groq {Model} with key [..{Suffix}] failed with {Status}: {Body}",
                    model, suffix, (int)response.StatusCode, body.Length <= 300 ? body : body[..300]);
                if (response.StatusCode == HttpStatusCode.BadRequest)
                {
                    return null; // the request itself is wrong: another key will not help
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning("X01 extraction: Groq key [..{Suffix}] timed out.", suffix);
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(ex, "X01 extraction: Groq unreachable with key [..{Suffix}].", suffix);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "X01 extraction: Groq answered with content that is not the schema's JSON.");
                return null;
            }
        }

        return null;
    }
}

/// <summary>
/// Tries the extractors in the configured order (AiCompliance:VoiceRegistration:Extractors,
/// default the Gate 0 winner first) and fails over to the next on any failure.
/// </summary>
public sealed class CompositeRegistrationExtractor(
    GeminiRegistrationExtractor gemini,
    GroqRegistrationExtractor groq,
    IConfiguration configuration,
    ILogger<CompositeRegistrationExtractor> logger) : IRegistrationExtractor
{
    public static readonly string[] DefaultOrder = ["groq", "gemini"];

    public bool AcceptsAudio => Order().Contains("gemini");

    public async Task<RegistrationExtraction> ExtractAsync(RegistrationExtractionInput input, CancellationToken ct)
    {
        foreach (var name in Order())
        {
            if (input.Audio is not null && name != "gemini")
            {
                continue; // only Gemini hears audio
            }

            var result = name == "gemini" ? await gemini.ExtractAsync(input, ct) : await groq.ExtractAsync(input, ct);
            if (result is not null)
            {
                return result;
            }

            logger.LogWarning("X01 extraction: {Extractor} gave no usable answer -- trying the next.", name);
        }

        throw new AiUnavailableException("Trợ lý chưa hiểu được lúc này. Bạn gõ giúp vào ô bên dưới nhé.");
    }

    private string[] Order() =>
        configuration.GetSection("AiCompliance:VoiceRegistration:Extractors").Get<string[]>() is { Length: > 0 } configured
            ? configured.Select(x => x.Trim().ToLowerInvariant()).Where(x => x is "gemini" or "groq").Distinct().ToArray()
            : DefaultOrder;
}
