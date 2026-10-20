using System.Buffers;
using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using StreetBiz.Application.Features.Chatbot;

namespace StreetBiz.API.Extensions;

public static class ChatbotVoiceApi
{
    public static bool VoiceAvailable(ChatbotSettings settings, ChatbotVoiceSettings voice, IEnumerable<IChatbotVoiceProvider> providers, ChatbotActor actor) =>
        settings.Enabled && voice.Enabled && actor.UserId != 0 && providers.Any(p => p.Available);

    public static void MapChatbotVoiceApi(this WebApplication app, RouteGroupBuilder account)
    {
        account.MapPost("/voice/sessions", async (ChatbotVoiceStartRequest request, ChatbotSettings settings, ChatbotVoiceSettings voice,
            IEnumerable<IChatbotVoiceProvider> providers, IChatbotActorResolver actors, IChatbotStore store, ChatbotVoiceSessions sessions,
            HttpContext context, CancellationToken ct) =>
        {
            var actor = await actors.RequireAsync(ct);
            if (!VoiceAvailable(settings, voice, providers, actor))
                throw new ChatbotException(503, "voice_unavailable", "Chế độ giọng nói chưa được bật. Bạn vẫn có thể nhắn tin với trợ lý.");
            if (!Guid.TryParse(request.ClientRequestId, out _)) throw new ChatbotException(400, "invalid_request", "Mã yêu cầu không hợp lệ.");
            if (request.Location is { } at && (at.Latitude is < -90 or > 90 || at.Longitude is < -180 or > 180))
                throw new ChatbotException(400, "invalid_location", "Vị trí không hợp lệ.");
            var used = await store.VoiceSecondsTodayAsync(actor.UserId, ct);
            if (sessions.RemainingSeconds(used) < 10)
                throw new ChatbotException(429, "voice_daily_limit", "Bạn đã dùng hết thời lượng trò chuyện bằng giọng nói hôm nay. Bạn vẫn có thể nhắn tin.");
            var conversation = await store.CreateAsync(actor, request.ClientRequestId, ct);
            var ticket = sessions.Issue(actor, conversation.Id, ChatbotPrivacy.Coarse(request.Location), request.Simple, used);
            return Results.Json(ChatbotApi.Envelope(new
            {
                ticket.SessionId, ticket = ticket.Secret, ticket.ConversationId, ticket.ExpiresAt,
                maxSeconds = sessions.RemainingSeconds(used), inputSampleRate = 16000, outputSampleRate = 24000,
                streamPath = $"/hubs/chatbot-voice/{ticket.SessionId}",
            }, null, context), statusCode: 201);
        }).RequireRateLimiting("ChatbotAi");

        // Under /hubs so the browser can authenticate the upgrade with ?access_token=, like SignalR.
        app.MapGet("/hubs/chatbot-voice/{sessionId}", async (string sessionId, string? ticket, HttpContext context,
            ChatbotVoiceSessions sessions, IChatbotActorResolver actors, ChatbotVoiceService service) =>
        {
            if (!context.WebSockets.IsWebSocketRequest) return Results.StatusCode(StatusCodes.Status426UpgradeRequired);
            var redeemed = sessions.Redeem(sessionId, ticket);
            if (redeemed is null) return Results.StatusCode(StatusCodes.Status401Unauthorized);
            ChatbotActor caller;
            try { caller = await actors.RequireAsync(context.RequestAborted); }
            catch (ChatbotException) { return Results.StatusCode(StatusCodes.Status401Unauthorized); }
            // A ticket is bound to the account, login session and scope that requested it.
            if (caller.UserId != redeemed.Actor.UserId || caller.SessionId != redeemed.Actor.SessionId || caller.Scope != redeemed.Actor.Scope)
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var client = new WebSocketVoiceClient(socket);
            await service.RunAsync(redeemed, client, context.RequestAborted);
            await client.CloseAsync();
            return Results.Empty;
        }).RequireAuthorization().RequireRateLimiting("ChatbotStorage").ExcludeFromDescription();
    }
}

/// <summary>Browser protocol: binary = PCM16 16 kHz mono in, [uint32 generation LE + PCM16 24 kHz] out; text = JSON control/events.</summary>
public sealed class WebSocketVoiceClient(WebSocket socket) : IChatbotVoiceClient
{
    private const int MaxFrameBytes = 64 * 1024;
    private readonly SemaphoreSlim sending = new(1, 1);

    public async Task<ChatbotVoiceClientFrame?> ReceiveAsync(CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            using var message = new MemoryStream();
            while (true)
            {
                WebSocketReceiveResult result;
                try { result = await socket.ReceiveAsync(buffer, ct); }
                catch (WebSocketException) { return null; }
                if (result.MessageType == WebSocketMessageType.Close) return null;
                message.Write(buffer, 0, result.Count);
                if (message.Length > MaxFrameBytes) return null;
                if (!result.EndOfMessage) continue;
                if (result.MessageType == WebSocketMessageType.Binary) return new ChatbotVoiceClientAudio(message.ToArray());
                try
                {
                    using var json = JsonDocument.Parse(message.ToArray());
                    var root = json.RootElement;
                    var type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "";
                    bool? muted = root.TryGetProperty("muted", out var m) && m.ValueKind is JsonValueKind.True or JsonValueKind.False ? m.GetBoolean() : null;
                    return new ChatbotVoiceClientControl(type, muted);
                }
                catch (JsonException) { return new ChatbotVoiceClientControl("invalid"); }
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    public async Task SendAsync(object message, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, ChatbotJson.Options);
        await Send(bytes, WebSocketMessageType.Text, ct);
    }

    public async Task SendAudioAsync(int generation, ReadOnlyMemory<byte> pcm, CancellationToken ct)
    {
        var frame = new byte[4 + pcm.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)generation);
        pcm.CopyTo(frame.AsMemory(4));
        await Send(frame, WebSocketMessageType.Binary, ct);
    }

    private async Task Send(byte[] bytes, WebSocketMessageType type, CancellationToken ct)
    {
        await sending.WaitAsync(ct);
        try
        {
            if (socket.State == WebSocketState.Open) await socket.SendAsync(bytes, type, true, ct);
        }
        finally { sending.Release(); }
    }

    public async Task CloseAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "ended", timeout.Token);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
    }
}
