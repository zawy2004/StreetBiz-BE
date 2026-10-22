using System.Security.Claims;
using System.Threading.RateLimiting;

namespace StreetBiz.API.Extensions;

/// <summary>
/// One limit for every endpoint that calls a generative model (AIP-01..04, AIB-01): each call costs money and
/// time, so a signed-in user gets a modest budget and a guest, counted by address, a smaller one. A request over
/// the limit gets 429; the screens then fall back to typing or the built-in keyword search.
/// </summary>
public static class AiRateLimitSetup
{
    public const string Policy = "AiAssist";

    public static IServiceCollection AddAiRateLimit(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.AddPolicy(Policy, context =>
            {
                var userId = context.User.FindFirstValue("sub");
                return RateLimitPartition.GetFixedWindowLimiter(
                    userId is not null ? $"ai-user:{userId}" : $"ai-guest:{context.Connection.RemoteIpAddress}",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = userId is not null ? 20 : 8,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    });
            });
        });
        return services;
    }
}
