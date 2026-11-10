using System.Security.Claims;
using System.Threading.RateLimiting;

namespace StreetBiz.API.Extensions;

public static class CommunityApiSetup
{
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
            options.AddPolicy("CommunityReport", context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.User.FindFirstValue("sub")
                    ?? context.Connection.RemoteIpAddress?.ToString()
                    ?? "anonymous",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 5,
                        Window = TimeSpan.FromHours(1),
                        QueueLimit = 0,
                    }));
        });
        return services;
    }
}
