using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace StreetBiz.Infrastructure.Services;

/// <summary>
/// Shared Gemini Vision caller, extracted out of AiComplianceService so a second caller
/// (FptAiKycService's fallback) does not duplicate the key-pool rotation, 503-retry and
/// 429/403-failover logic. Behavior is unchanged from before the extraction -- callers that
/// only had one image keep passing exactly one.
/// </summary>
public sealed class GeminiVisionClient(
    HttpClient httpClient,
    IConfiguration configuration,
    ILogger<GeminiVisionClient> logger,
    AiKeyPools keyPools)
{
    public async Task<(bool Success, string? Content)> CallAsync(
        string prompt, IReadOnlyList<(byte[] Bytes, string MimeType)> images, CancellationToken ct)
    {
        var geminiKeyPool = keyPools.Gemini;
        if (!geminiKeyPool.HasKeys)
        {
            logger.LogWarning("No Gemini API keys configured in AiCompliance:Gemini:ApiKeys or ApiKey.");
            return (false, null);
        }

        var model = configuration["AiCompliance:Gemini:Model"]?.Trim();
        // gemini-2.0-flash was retired by Google (confirmed 2026-09-20: the API now returns
        // 404 "model no longer available, use gemini-3.6-flash"), which silently broke both
        // CCCD OCR extraction and encroachment-photo analysis -- both always fell through to
        // an empty/failed result even with valid keys, since this is their shared default.
        if (string.IsNullOrWhiteSpace(model)) model = "gemini-3.6-flash";

        var maxTokensStr = configuration["AiCompliance:Gemini:MaxOutputTokens"]?.Trim();
        var maxOutputTokens = int.TryParse(maxTokensStr, out var m) && m > 0 ? m : 4096;

        var parts = new List<object> { new { text = prompt } };
        parts.AddRange(images.Select(img => (object)new
        {
            inline_data = new { mime_type = img.MimeType, data = Convert.ToBase64String(img.Bytes) }
        }));

        var payload = new
        {
            contents = new[] { new { parts = parts.ToArray() } },
            generationConfig = new
            {
                temperature = 0.1,
                maxOutputTokens,
                responseMimeType = "application/json"
            }
        };

        var payloadJson = JsonSerializer.Serialize(payload);
        var keysToTry = geminiKeyPool.GetAllKeysInOrder();

        for (int attempt = 0; attempt < keysToTry.Length; attempt++)
        {
            var apiKey = keysToTry[attempt];
            try
            {
                var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";

                // Google's 503 "currently experiencing high demand" is a transient, same-key
                // condition (confirmed 2026-09-20: retrying the identical request against the
                // identical key succeeded 2 of the next 3 tries) -- distinct from 429/403, which
                // mean THIS key is out of quota and only switching keys helps. With a single
                // configured key (this project's common case), the old code had no recourse for
                // a transient 503 at all and just failed the whole extraction.
                HttpResponseMessage response;
                string responseBody;
                const int maxTransientRetries = 2;
                var transientRetry = 0;
                while (true)
                {
                    using var content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
                    response = await httpClient.PostAsync(url, content, ct);
                    if (response.StatusCode != System.Net.HttpStatusCode.ServiceUnavailable || transientRetry >= maxTransientRetries)
                    {
                        break;
                    }
                    transientRetry++;
                    logger.LogInformation(
                        "Gemini Vision returned 503 (transient overload) -- retrying same key in 1s ({Retry}/{Max})...",
                        transientRetry, maxTransientRetries);
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                }

                if (response.IsSuccessStatusCode)
                {
                    responseBody = await response.Content.ReadAsStringAsync(ct);
                    var doc = JsonNode.Parse(responseBody);
                    var text = doc?["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.GetValue<string>();
                    return (!string.IsNullOrWhiteSpace(text), text);
                }

                var error = await response.Content.ReadAsStringAsync(ct);
                logger.LogWarning(
                    "Gemini Vision API call with key [..{KeySuffix}] failed with code {StatusCode}: {Error}",
                    apiKey.Length > 8 ? apiKey[^8..] : apiKey,
                    response.StatusCode,
                    error);

                // If 429 (Too Many Requests), 403 (Quota Limit), or 503 (still overloaded after
                // the same-key retries above), try next key in pool
                if ((int)response.StatusCode == 429 || (int)response.StatusCode == 403 || (int)response.StatusCode == 503)
                {
                    if (attempt < keysToTry.Length - 1)
                    {
                        logger.LogInformation("Failing over to next Gemini API key in pool (attempt {Attempt}/{Total})...", attempt + 2, keysToTry.Length);
                        continue;
                    }
                }

                return (false, null);
            }
            catch (Exception ex) when (attempt < keysToTry.Length - 1)
            {
                logger.LogWarning(ex, "Exception calling Gemini Vision API with key [..{KeySuffix}], failing over to next key...", apiKey.Length > 8 ? apiKey[^8..] : apiKey);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Exception calling Gemini Vision API after trying {Total} keys.", keysToTry.Length);
                return (false, null);
            }
        }

        return (false, null);
    }
}
