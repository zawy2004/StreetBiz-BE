using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Features.Chatbot;
using StreetBiz.Application.Features.VendorRegistration.AssistTurn;

namespace StreetBiz.API.Extensions;

/// <summary>
/// X01 v2: the live registration interview stream. Same wire protocol as the chatbot voice relay
/// (<see cref="WebSocketVoiceClient"/>); the ticket comes from POST api/vendor/registrations/assist/live.
/// </summary>
public static class RegistrationVoiceApi
{
    public static void MapRegistrationVoiceApi(this WebApplication app)
    {
        // Under /hubs so the browser can authenticate the upgrade with ?access_token=, like SignalR.
        app.MapGet("/hubs/registration-voice/{sessionId}", async (string sessionId, string? ticket, HttpContext context,
            IConfiguration configuration, RegistrationVoiceTickets tickets, IChatbotActorResolver actors, RegistrationVoiceService service) =>
        {
            if (!configuration.GetValue("Features:VoiceRegistration", true)) return Results.NotFound();
            if (!context.WebSockets.IsWebSocketRequest) return Results.StatusCode(StatusCodes.Status426UpgradeRequired);
            var redeemed = tickets.Redeem(sessionId, ticket);
            if (redeemed is null) return Results.StatusCode(StatusCodes.Status401Unauthorized);
            ChatbotActor caller;
            try { caller = await actors.RequireAsync(context.RequestAborted); }
            catch (Exception ex) when (ex is ChatbotException or AppException) { return Results.StatusCode(StatusCodes.Status401Unauthorized); }
            // A ticket is bound to the account, login session and scope that requested it.
            if (caller.UserId != redeemed.Actor.UserId || caller.SessionId != redeemed.Actor.SessionId || caller.Scope != redeemed.Actor.Scope)
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var client = new WebSocketVoiceClient(socket);
            await service.RunAsync(redeemed, client, context.RequestAborted);
            await client.CloseAsync();
            return Results.Empty;
        }).RequireAuthorization().RequireRateLimiting("VendorVoiceAi").ExcludeFromDescription();
    }
}
