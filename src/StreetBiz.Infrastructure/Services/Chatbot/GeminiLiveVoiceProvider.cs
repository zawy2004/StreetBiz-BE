using System.Buffers;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StreetBiz.Application.Features.Chatbot;

namespace StreetBiz.Infrastructure.Services.Chatbot;

/// <summary>
/// Gemini Live (bidirectional speech) over the documented BidiGenerateContent WebSocket.
/// Model and voice come from configuration; verify them against the current Live API docs before enabling.
/// </summary>
public sealed class GeminiLiveVoiceProvider(AiKeyPools keys, ChatbotVoiceSettings settings, ChatbotProviderHealth health) : IChatbotVoiceProvider
{
    private const string Endpoint = "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent";
    public string Name => "GEMINI_LIVE";
    public bool Available => settings.Enabled && keys.Gemini.HasKeys && !string.IsNullOrWhiteSpace(settings.Model);

    public static JsonObject Setup(ChatbotVoiceSettings settings, ChatbotVoiceSetup setup, string? resumeHandle = null)
    {
        var declarations = new JsonArray();
        foreach (var tool in setup.Tools)
            declarations.Add(new JsonObject
            {
                ["name"] = tool.Name.Replace(".", "__"),
                ["description"] = tool.Description,
                ["parametersJsonSchema"] = JsonNode.Parse(tool.Parameters.GetRawText()),
            });
        var body = new JsonObject
        {
            ["model"] = "models/" + settings.Model,
            ["generationConfig"] = new JsonObject
            {
                ["responseModalities"] = new JsonArray("AUDIO"),
                ["speechConfig"] = new JsonObject
                {
                    ["voiceConfig"] = new JsonObject { ["prebuiltVoiceConfig"] = new JsonObject { ["voiceName"] = settings.VoiceName } },
                },
            },
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = setup.SystemPrompt }) },
            ["realtimeInputConfig"] = new JsonObject
            {
                ["automaticActivityDetection"] = new JsonObject { ["silenceDurationMs"] = settings.SilenceDurationMs, ["prefixPaddingMs"] = 200 },
            },
            ["inputAudioTranscription"] = new JsonObject(),
            ["outputAudioTranscription"] = new JsonObject(),
            ["contextWindowCompression"] = new JsonObject { ["slidingWindow"] = new JsonObject() },
            // Ask for resumption handles so a dropped connection can continue the same conversation.
            ["sessionResumption"] = resumeHandle is null ? new JsonObject() : new JsonObject { ["handle"] = resumeHandle },
        };
        if (declarations.Count > 0) body["tools"] = new JsonArray(new JsonObject { ["functionDeclarations"] = declarations });
        return new JsonObject { ["setup"] = body };
    }

    public async Task<IChatbotVoiceUpstream> ConnectAsync(ChatbotVoiceSetup setup, CancellationToken ct, string? resumeHandle = null)
    {
        if (!Available) throw new ChatbotProviderException("provider_configuration", false);
        if (!health.Ready(Name)) throw new ChatbotProviderException("cooldown", true);
        ChatbotProviderException last = new("provider_unavailable", true);
        foreach (var key in keys.Gemini.GetAllKeysInOrder().Take(3))
        {
            var credential = Name + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
            if (!health.Ready(credential)) continue;
            var socket = new ClientWebSocket();
            // Header, not the documented ?key= query form, so the credential never appears in a URL.
            socket.Options.SetRequestHeader("x-goog-api-key", key);
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attempt.CancelAfter(TimeSpan.FromSeconds(12));
            try
            {
                await socket.ConnectAsync(new Uri(Endpoint), attempt.Token);
                var upstream = new GeminiLiveUpstream(socket, settings.Model);
                await upstream.SendJsonAsync(Setup(settings, setup, resumeHandle), attempt.Token);
                await upstream.WaitForSetupAsync(attempt.Token);
                return upstream;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                health.Cooldown(credential, TimeSpan.FromSeconds(15)); last = new("provider_timeout", true);
            }
            catch (WebSocketException)
            {
                health.Cooldown(credential, TimeSpan.FromSeconds(30)); last = new("provider_unavailable", true);
            }
            catch (ChatbotProviderException ex)
            {
                if (ex.Category == "provider_quota") health.Cooldown(credential, TimeSpan.FromSeconds(60));
                else if (ex.Category == "provider_credentials") health.Cooldown(credential, TimeSpan.FromMinutes(5));
                last = ex;
                if (ex.Category == "provider_configuration") { socket.Dispose(); throw; }
            }
            socket.Dispose();
        }
        throw last;
    }
}

public sealed class GeminiLiveUpstream(WebSocket socket, string model) : IChatbotVoiceUpstream
{
    private const int MaxMessageBytes = 4 * 1024 * 1024;
    private readonly SemaphoreSlim sending = new(1, 1);
    public string Model => model;

    public async Task SendJsonAsync(JsonNode message, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await sending.WaitAsync(ct);
        try { await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct); }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException) { throw new ChatbotProviderException("network", true); }
        finally { sending.Release(); }
    }

    public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16k, CancellationToken ct) => SendJsonAsync(new JsonObject
    {
        ["realtimeInput"] = new JsonObject
        {
            ["audio"] = new JsonObject { ["data"] = Convert.ToBase64String(pcm16k.Span), ["mimeType"] = "audio/pcm;rate=16000" },
        },
    }, ct);

    public Task SendAudioEndAsync(CancellationToken ct) =>
        SendJsonAsync(new JsonObject { ["realtimeInput"] = new JsonObject { ["audioStreamEnd"] = true } }, ct);

    public Task SendToolResponsesAsync(IReadOnlyList<ChatbotToolReply> replies, CancellationToken ct)
    {
        var responses = new JsonArray();
        foreach (var reply in replies)
            responses.Add(new JsonObject
            {
                ["id"] = reply.Id,
                ["name"] = reply.Name.Replace(".", "__"),
                ["response"] = new JsonObject { ["result"] = JsonNode.Parse(reply.Json) },
            });
        return SendJsonAsync(new JsonObject { ["toolResponse"] = new JsonObject { ["functionResponses"] = responses } }, ct);
    }

    public async Task WaitForSetupAsync(CancellationToken ct)
    {
        var message = await ReceiveAsync(ct);
        if (message is null) throw CloseError();
        using (message)
            if (!message.RootElement.TryGetProperty("setupComplete", out _)) throw new ChatbotProviderException("provider_configuration", false);
    }

    /// <summary>Close codes from the service explain setup failures (bad model, key or quota) without exposing details.</summary>
    private ChatbotProviderException CloseError()
    {
        var reason = (socket.CloseStatusDescription ?? "").ToLowerInvariant();
        if (reason.Contains("quota") || reason.Contains("resource_exhausted") || reason.Contains("rate")) return new("provider_quota", true);
        if (reason.Contains("api key") || reason.Contains("permission") || reason.Contains("unauthenticated")) return new("provider_credentials", true);
        if (socket.CloseStatus is WebSocketCloseStatus.PolicyViolation or WebSocketCloseStatus.InvalidPayloadData || reason.Contains("model") || reason.Contains("invalid"))
            return new("provider_configuration", false);
        return new("provider_unavailable", true);
    }

    private async Task<JsonDocument?> ReceiveAsync(CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            using var message = new MemoryStream();
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                message.Write(buffer, 0, result.Count);
                if (message.Length > MaxMessageBytes) throw new ChatbotProviderException("frame_too_large", false);
                // The service may deliver JSON in text or binary frames.
                if (result.EndOfMessage) return JsonDocument.Parse(message.ToArray());
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    public async IAsyncEnumerable<ChatbotVoiceEvent> EventsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            JsonDocument? message;
            try { message = await ReceiveAsync(ct); }
            catch (WebSocketException) { throw new ChatbotProviderException("network", true); }
            if (message is null)
            {
                // An abnormal close is worth a resume; a normal one means the session is over.
                if (socket.CloseStatus is not (WebSocketCloseStatus.NormalClosure or null)) throw new ChatbotProviderException("network", true);
                yield break;
            }
            using (message)
                foreach (var item in Map(message.RootElement)) yield return item;
        }
    }

    public static IEnumerable<ChatbotVoiceEvent> Map(JsonElement root)
    {
        if (root.TryGetProperty("serverContent", out var content))
        {
            if (content.TryGetProperty("interrupted", out var interrupted) && interrupted.ValueKind == JsonValueKind.True)
                yield return new ChatbotVoiceInterrupted();
            if (content.TryGetProperty("inputTranscription", out var input) && Text(input) is { Length: > 0 } heard)
                yield return new ChatbotVoiceInputText(heard);
            if (content.TryGetProperty("modelTurn", out var turn) && turn.TryGetProperty("parts", out var parts))
                foreach (var part in parts.EnumerateArray())
                    if (part.TryGetProperty("inlineData", out var data) && data.TryGetProperty("data", out var b64)
                        && (!data.TryGetProperty("mimeType", out var mime) || mime.GetString()?.StartsWith("audio/", StringComparison.Ordinal) != false))
                        yield return new ChatbotVoiceAudio(Convert.FromBase64String(b64.GetString()!));
            if (content.TryGetProperty("outputTranscription", out var output) && Text(output) is { Length: > 0 } said)
                yield return new ChatbotVoiceOutputText(said);
            if (content.TryGetProperty("turnComplete", out var complete) && complete.ValueKind == JsonValueKind.True)
                yield return new ChatbotVoiceTurnComplete();
        }
        if (root.TryGetProperty("toolCall", out var toolCall) && toolCall.TryGetProperty("functionCalls", out var calls))
            yield return new ChatbotVoiceToolCalls(calls.EnumerateArray().Select(call => new ChatbotToolCall(
                call.TryGetProperty("id", out var id) ? id.GetString() ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N"),
                call.GetProperty("name").GetString()!.Replace("__", "."),
                call.TryGetProperty("args", out var args) ? args.GetRawText() : "{}")).ToArray());
        if (root.TryGetProperty("toolCallCancellation", out var cancellation) && cancellation.TryGetProperty("ids", out var ids))
            yield return new ChatbotVoiceToolCancelled(ids.EnumerateArray().Select(i => i.GetString()!).Where(i => i is not null).ToArray());
        if (root.TryGetProperty("sessionResumptionUpdate", out var resumption)
            && resumption.TryGetProperty("resumable", out var resumable) && resumable.ValueKind == JsonValueKind.True
            && resumption.TryGetProperty("newHandle", out var newHandle) && newHandle.GetString() is { Length: > 0 } handle)
            yield return new ChatbotVoiceResumeHandle(handle);
        if (root.TryGetProperty("goAway", out _)) yield return new ChatbotVoiceGoAway();
        if (root.TryGetProperty("usageMetadata", out var usage))
            yield return new ChatbotVoiceUsage(new(
                usage.TryGetProperty("promptTokenCount", out var p) && p.TryGetInt32(out var pi) ? pi : 0,
                usage.TryGetProperty("responseTokenCount", out var r) && r.TryGetInt32(out var ri) ? ri : 0));
    }

    private static string? Text(JsonElement transcription) =>
        transcription.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;

    public async ValueTask DisposeAsync()
    {
        if (socket.State == WebSocketState.Open)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "ended", timeout.Token); }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
        }
        socket.Dispose();
        sending.Dispose();
    }
}
