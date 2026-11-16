using System.Security.Claims;
using System.Threading.RateLimiting;

namespace StreetBiz.API.Extensions;

public static class CommunityApiSetup
{
    /// <summary>Reports an hour from one signed-in customer.</summary>
    public const int ReportsPerHourSignedIn = 5;

    /// <summary>
    /// Reports an hour from one IP address without a session (BUY-05 guests). Lower than a customer's:
    /// a guest has no account to answer for, and the one-pending-report-per-vendor rule cannot apply.
    /// </summary>
    public const int ReportsPerHourGuest = 3;

    public static IServiceCollection AddCommunityApi(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.AddPolicy("PublicPermitCheck", context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.User.FindFirstValue("sub")
                    ?? context.Connection.RemoteIpAddress?.ToString()
                    ?? "anonymous",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 30,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    }));
            // BUY-05/07: every report lands in a ward officer's inbox and notifies the vendor. Five an
            // hour per account is far above a real customer's need and well below a nuisance campaign.
            // Guests are counted per IP address (the real one: Caddy's X-Forwarded-For is trusted) in a
            // bucket of their own, so a guest never uses up a signed-in customer's allowance.
            options.AddPolicy("CommunityReport", context =>
            {
                var account = context.User.FindFirstValue("sub") ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier);
                return account is not null
                    ? RateLimitPartition.GetFixedWindowLimiter("user:" + account, _ => Hourly(ReportsPerHourSignedIn))
                    : RateLimitPartition.GetFixedWindowLimiter(
                        "guest:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
                        _ => Hourly(ReportsPerHourGuest));
            });
        });
        return services;
    }

    private static FixedWindowRateLimiterOptions Hourly(int permits) => new()
    {
        PermitLimit = permits,
        Window = TimeSpan.FromHours(1),
        QueueLimit = 0,
    };
}
