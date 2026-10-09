using System.Security.Claims;
using System.Threading.RateLimiting;
using FluentValidation;
using StreetBiz.API.Hubs;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Features.Chatbot;

namespace StreetBiz.API.Extensions;

public sealed record CreateChatbotConversation(string ClientRequestId);
public sealed record ChatbotFeedbackRequest(bool Helpful, string? Reason);

public static class ChatbotApi
{
    public static object Envelope(object? data, object? error, HttpContext context) => new { data, error, meta = new { traceId = context.TraceIdentifier } };
    private static IResult Ok(object? data, HttpContext context) => Results.Json(Envelope(data, null, context));

    public static IServiceCollection AddChatbotApi(this IServiceCollection services)
    {
        services.AddSingleton<ChatbotSubscriptions>();
        services.AddSingleton<IChatbotEvents, ChatbotEventPublisher>();
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = 429;
            options.AddPolicy("ChatbotAi", context => RateLimitPartition.GetFixedWindowLimiter(
                context.User.FindFirstValue("sub") is { } id ? $"user:{id}" : $"guest:{context.Connection.RemoteIpAddress}",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = context.User.Identity?.IsAuthenticated == true ? 10 : 3, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            options.AddPolicy("ChatbotStorage", context => RateLimitPartition.GetFixedWindowLimiter(
                context.User.FindFirstValue("sub") ?? context.Connection.RemoteIpAddress?.ToString() ?? "guest",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
        return services;
    }

    public static void MapChatbotApi(this WebApplication app)
    {
        var group = app.MapGroup("/api/chatbot").WithTags("Chatbot");
        group.MapGet("/capabilities", async (ChatbotSettings settings, ChatbotVoiceSettings voice, IEnumerable<IChatbotVoiceProvider> voiceProviders,
            IChatbotActorResolver actors, IEnumerable<IChatbotModel> models, HttpContext context, CancellationToken ct) =>
        {
            var actor = context.User.Identity?.IsAuthenticated == true ? await actors.RequireAsync(ct) : ChatbotActor.Guest;
            return Ok(new { settings.Enabled, settings.GuestEnabled, attachmentsEnabled = settings.Enabled && settings.AttachmentsEnabled && actor.UserId != 0 && models.Any(m => m.Name == "GEMINI" && m.Available), role = actor.Role,
                voiceEnabled = ChatbotVoiceApi.VoiceAvailable(settings, voice, voiceProviders, actor), voiceMaxSeconds = voice.MaxSessionSeconds,
                maxQuestionCharacters = settings.MaxQuestionCharacters, retentionDays = settings.RetentionDays,
                actions = ChatbotNavigation.ForRole(actor.Role), knowledgeVersion = ChatbotKnowledge.Version }, context);
        }).AllowAnonymous().RequireRateLimiting("ChatbotStorage");
        group.MapPost("/guest/messages", (ChatbotGuestRequest request, ChatbotService service, HttpContext context) => Ok(service.Guest(request), context))
            .AllowAnonymous().RequireRateLimiting("ChatbotAi");
        var account = group.MapGroup("").RequireAuthorization();
        account.MapPost("/attachments", async (ChatbotUploadRequest request, IChatbotActorResolver actors, IChatbotAttachments attachments, HttpContext context, CancellationToken ct) =>
            Ok(attachments.Upload(await actors.RequireAsync(ct), request), context)).RequireRateLimiting("ChatbotAi");
        account.MapDelete("/attachments/{id}", async (string id, IChatbotActorResolver actors, IChatbotAttachments attachments, HttpContext context, CancellationToken ct) =>
        {
            attachments.Remove(await actors.RequireAsync(ct), id);
            return Ok(new { deleted = true }, context);
        }).RequireRateLimiting("ChatbotStorage");
        account.MapGet("/briefing", async (ChatbotService service, IChatbotActorResolver actors, ChatbotTools tools, HttpContext context, CancellationToken ct) =>
        {
            service.RequireEnabled();
            return Ok(await tools.BriefingAsync(await actors.RequireAsync(ct), ct), context);
        }).RequireRateLimiting("ChatbotStorage");
        account.MapPost("/conversations", async (CreateChatbotConversation request, ChatbotService service, IChatbotActorResolver actors, IChatbotStore store, HttpContext context, CancellationToken ct) =>
        {
            service.RequireEnabled();
            var result = await store.CreateAsync(await actors.RequireAsync(ct), request.ClientRequestId, ct);
            return Results.Json(Envelope(result, null, context), statusCode: 201);
        }).RequireRateLimiting("ChatbotStorage");
        account.MapGet("/conversations", async (string? before, IChatbotActorResolver actors, IChatbotStore store, HttpContext context, CancellationToken ct) =>
            Ok(await store.ListAsync(await actors.RequireAsync(ct), before, ct), context)).RequireRateLimiting("ChatbotStorage");
        account.MapGet("/conversations/{id}/messages", async (string id, long? before, IChatbotActorResolver actors, IChatbotStore store, HttpContext context, CancellationToken ct) =>
            Ok(await store.HistoryAsync(await actors.RequireAsync(ct), id, before, ct), context)).RequireRateLimiting("ChatbotStorage");
        account.MapGet("/conversations/{id}/messages/{messageId}", async (string id, string messageId, IChatbotActorResolver actors, IChatbotStore store, HttpContext context, CancellationToken ct) =>
            Ok(await store.MessageAsync(await actors.RequireAsync(ct), id, messageId, ct), context)).RequireRateLimiting("ChatbotStorage");
        account.MapPost("/conversations/{id}/messages", async (string id, ChatbotSendRequest request, ChatbotService service, HttpContext context, CancellationToken ct) =>
            Ok(await service.SendAsync(id, request, ct), context)).RequireRateLimiting("ChatbotAi");
        account.MapPost("/conversations/{id}/messages/{messageId}/cancel", async (string id, string messageId, IChatbotActorResolver actors, IChatbotStore store, ChatbotRuntime runtime, HttpContext context, CancellationToken ct) =>
        {
            var actor = await actors.RequireAsync(ct);
            await store.CancelAsync(actor, id, messageId, ct);
            runtime.Cancel(messageId);
            return Ok(await store.MessageAsync(actor, id, messageId, ct), context);
        }).RequireRateLimiting("ChatbotStorage");
        account.MapDelete("/conversations/{id}", async (string id, IChatbotActorResolver actors, IChatbotStore store, ChatbotRuntime runtime, HttpContext context, CancellationToken ct) =>
        {
            var actor = await actors.RequireAsync(ct);
            var conversation = await store.RequireAsync(actor, id, ct);
            await store.DeleteAsync(actor, id, ct);
            if (conversation.ActiveMessageId is { } active) runtime.Cancel(active);
            return Ok(new { deleted = true }, context);
        }).RequireRateLimiting("ChatbotStorage");
        account.MapPost("/conversations/{id}/messages/{messageId}/feedback", async (string id, string messageId, ChatbotFeedbackRequest request, IChatbotActorResolver actors, IChatbotStore store, HttpContext context, CancellationToken ct) =>
        {
            await store.FeedbackAsync(await actors.RequireAsync(ct), id, messageId, request.Helpful, request.Reason, ct);
            return Ok(new { saved = true }, context);
        }).RequireRateLimiting("ChatbotStorage");
        app.MapChatbotVoiceApi(account);
        app.MapHub<ChatbotHub>("/hubs/chatbot", options => options.CloseOnAuthenticationExpiration = true);
    }
}

/// <summary>Only chatbot HTTP endpoints get the new envelope. Existing controller contracts are unchanged.</summary>
public sealed class ChatbotEnvelopeMiddleware(RequestDelegate next, ILogger<ChatbotEnvelopeMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api/chatbot")) { await next(context); return; }
        context.Response.Headers.CacheControl = "no-store, private";
        context.Response.Headers.Pragma = "no-cache";
        var limit = context.Request.Path == "/api/chatbot/attachments" ? 2_810_000 : 16000;
        var sizeFeature = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = limit;
        if (context.Request.ContentLength > limit)
        {
            context.Response.StatusCode = 413;
            await Write(context, "request_too_large", "Câu hỏi vượt quá kích thước cho phép."); return;
        }
        try
        {
            await next(context);
            if (!context.Response.HasStarted && context.Response.StatusCode >= 400)
            {
                if (context.Response.StatusCode == 429) context.Response.Headers.RetryAfter = "60";
                await Write(context, context.Response.StatusCode == 401 ? "session_expired" : "request_rejected", "Yêu cầu chưa được chấp nhận. Kiểm tra đăng nhập hoặc thử lại sau.");
            }
        }
        catch (Exception ex) when (!context.Response.HasStarted && ex is not OperationCanceledException)
        {
            var (status, code, message) = ex switch
            {
                ChatbotException e => (e.Status, e.Code, e.Message),
                AppException e => (e.StatusCode, e.ErrorCode, e.StatusCode is 403 or 404 ? "Không lấy được dữ liệu trong phạm vi tài khoản của bạn." : e.Message),
                ValidationException => (400, "invalid_request", "Thông tin yêu cầu chưa hợp lệ."),
                BadHttpRequestException => (400, "invalid_request", "Nội dung yêu cầu không hợp lệ."),
                _ => (503, "assistant_unavailable", "Trợ lý chưa sẵn sàng. Vui lòng thử lại sau hoặc mở màn hình nghiệp vụ."),
            };
            if (status >= 500) logger.LogError("Chatbot request failed: {Type}; trace {TraceId}", ex.GetType().Name, context.TraceIdentifier);
            context.Response.StatusCode = status;
            await context.Response.WriteAsJsonAsync(ChatbotApi.Envelope(null, new { code, message, activeMessageId = (ex as ChatbotException)?.ActiveMessageId }, context));
        }
    }
    private static Task Write(HttpContext context, string code, string message) => context.Response.WriteAsJsonAsync(ChatbotApi.Envelope(null, new { code, message }, context));
}
