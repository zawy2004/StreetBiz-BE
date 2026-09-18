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
        });
        return services;
    }
}
