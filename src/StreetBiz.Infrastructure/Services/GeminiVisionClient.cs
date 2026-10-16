using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace StreetBiz.Infrastructure.Services;

/// <summary>
/// Shared Gemini Vision caller (AiComplianceService and FptAiKycService's fallback), so the key
/// rotation and failover live in one place.
///
/// Order of attempts: the configured model (AiCompliance:Gemini:Model) with every key of the pool,
/// then each fallback model (AiCompliance:Gemini:FallbackModels, default gemini-3.6-flash) the same
/// way. What a failure says decides where to go next:
///   - 429 / 403 / 401: this key is out of quota or refused -- next key, same model.
///   - 503 / 500 / 504, timeout: the model is overloaded (confirmed 2026-10-16: every key got 503 on
///     gemini-3.8-flash while gemini-3.6-flash answered in 1.5 s) -- the other keys would get the same,
///     so go straight to the next model instead of sleeping and retrying.
///   - 404: the model was retired -- next model.
/// Each attempt is capped (AiCompliance:Gemini:AttemptTimeoutSeconds, default 10 s) so one hanging
/// call cannot eat the caller's whole budget, and a model that was overloaded or hung is skipped for
/// AiCompliance:Gemini:ModelCooldownSeconds (default 120 s), so the next requests go straight to a
/// model that answers instead of waiting on the hung one again. The key travels in the x-goog-api-key header, never in
/// the URL, and only its last 4 characters are logged.
/// </summary>
public sealed class GeminiVisionClient(
    HttpClient httpClient,
    IConfiguration configuration,
    ILogger<GeminiVisionClient> logger,
    AiKeyPools keyPools)
{
    private const string DefaultModel = "gemini-3.6-flash";

    public async Task<(bool Success, string? Content)> CallAsync(
        string prompt, IReadOnlyList<(byte[] Bytes, string MimeType)> images, CancellationToken ct)
    {
        var keys = keyPools.Gemini.GetAllKeysInOrder();
        if (keys.Length == 0)
        {
            logger.LogWarning("No Gemini API keys configured in AiCompliance:Gemini:ApiKeys or ApiKey.");
            return (false, null);
        }

        var models = Models();
        var attemptTimeout = TimeSpan.FromSeconds(PositiveInt("AiCompliance:Gemini:AttemptTimeoutSeconds", 10));
        var cooldown = TimeSpan.FromSeconds(PositiveInt("AiCompliance:Gemini:ModelCooldownSeconds", 120));
        var withThinking = PayloadJson(prompt, images, includeThinkingConfig: true);

        foreach (var model in models)
        {
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent";
            var payload = withThinking;

            for (var i = 0; i < keys.Length; i++)
            {
                var suffix = keys[i].Length > 4 ? keys[i][^4..] : "****";
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attempt.CancelAfter(attemptTimeout);
                try
                {
                    using var response = await SendAsync(url, keys[i], payload, attempt.Token);
                    var body = await response.Content.ReadAsStringAsync(attempt.Token);

                    // A model that does not take a thinking setting answers 400; ask again without it.
                    if (response.StatusCode == HttpStatusCode.BadRequest && payload == withThinking
                        && body.Contains("thinking", StringComparison.OrdinalIgnoreCase))
                    {
                        payload = PayloadJson(prompt, images, includeThinkingConfig: false);
                        i--;
                        continue;
                    }

                    if (response.IsSuccessStatusCode)
                    {
                        var text = JsonNode.Parse(body)?["candidates"]?[0]?["content"]?["parts"]?
                            .AsArray().Select(p => p?["text"]?.GetValue<string>()).LastOrDefault(t => !string.IsNullOrWhiteSpace(t));
                        return (!string.IsNullOrWhiteSpace(text), text);
                    }

                    logger.LogWarning(
                        "Gemini {Model} with key [..{KeySuffix}] failed with {StatusCode}: {Error}",
                        model, suffix, (int)response.StatusCode, Shorten(body));

                    if (IsKeyProblem(response.StatusCode))
                    {
                        continue; // next key, same model
                    }

                    if (IsModelProblem(response.StatusCode))
                    {
                        CoolDown(model, cooldown);
                        break; // next model
                    }

                    return (false, null); // 400 and the like: the request itself is wrong
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    logger.LogWarning(
                        "Gemini {Model} with key [..{KeySuffix}] did not answer within {Seconds} s -- trying the next model.",
                        model, suffix, attemptTimeout.TotalSeconds);
                    CoolDown(model, cooldown);
                    break;
                }
                catch (HttpRequestException ex)
                {
                    logger.LogWarning(ex, "Gemini {Model} with key [..{KeySuffix}] unreachable.", model, suffix);
                }
                catch (JsonException ex)
                {
                    logger.LogWarning(ex, "Gemini {Model} returned a body that is not JSON.", model);
                    return (false, null);
                }
            }
        }

        logger.LogWarning("Gemini: every key of every model ({Models}) failed.", string.Join(", ", models));
        return (false, null);
    }

    private async Task<HttpResponseMessage> SendAsync(string url, string apiKey, string payloadJson, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payloadJson, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("x-goog-api-key", apiKey);
        return await httpClient.SendAsync(request, ct);
    }

    /// <summary>The configured model first, then the fallbacks, without duplicates. Models that just
    /// failed go to the end (they are still tried when nothing else answers).</summary>
    private IReadOnlyList<string> Models()
    {
        var primary = configuration["AiCompliance:Gemini:Model"]?.Trim();
        var fallbacks = configuration.GetSection("AiCompliance:Gemini:FallbackModels").Get<string[]>()
            ?? [DefaultModel];
        var all = new[] { string.IsNullOrWhiteSpace(primary) ? DefaultModel : primary }
            .Concat(fallbacks.Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var now = DateTime.UtcNow;
        bool CoolingDown(string model) =>
            keyPools.GeminiModelCooldowns.TryGetValue(model, out var until) && until > now;
        return all.Where(m => !CoolingDown(m)).Concat(all.Where(CoolingDown)).ToArray();
    }

    private void CoolDown(string model, TimeSpan cooldown)
    {
        keyPools.GeminiModelCooldowns[model] = DateTime.UtcNow + cooldown;
        logger.LogInformation("Gemini {Model} is skipped for {Seconds} s.", model, cooldown.TotalSeconds);
    }

    private string PayloadJson(string prompt, IReadOnlyList<(byte[] Bytes, string MimeType)> images, bool includeThinkingConfig)
    {
        var parts = new List<object> { new { text = prompt } };
        parts.AddRange(images.Select(img => (object)new
        {
            inline_data = new { mime_type = img.MimeType, data = Convert.ToBase64String(img.Bytes) }
        }));

        var generationConfig = new Dictionary<string, object>
        {
            ["temperature"] = 0.1,
            ["maxOutputTokens"] = PositiveInt("AiCompliance:Gemini:MaxOutputTokens", 4096),
            ["responseMimeType"] = "application/json",
        };
        if (includeThinkingConfig)
        {
            // Reading a card is transcription, not reasoning: thinking only adds latency (and its
            // tokens count against maxOutputTokens). AiCompliance:Gemini:ThinkingBudget overrides.
            var budget = int.TryParse(configuration["AiCompliance:Gemini:ThinkingBudget"], out var b) && b >= 0 ? b : 0;
            generationConfig["thinkingConfig"] = new { thinkingBudget = budget };
        }

        return JsonSerializer.Serialize(new
        {
            contents = new[] { new { parts = parts.ToArray() } },
            generationConfig,
        });
    }

    private int PositiveInt(string key, int fallback) =>
        int.TryParse(configuration[key]?.Trim(), out var value) && value > 0 ? value : fallback;

    private static bool IsKeyProblem(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized;

    private static bool IsModelProblem(HttpStatusCode status) =>
        status is HttpStatusCode.NotFound || (int)status >= 500;

    private static string Shorten(string body) => body.Length <= 300 ? body : body[..300] + "…";
}
