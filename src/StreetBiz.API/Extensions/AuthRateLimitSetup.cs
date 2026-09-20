using System.Threading.RateLimiting;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.API.Extensions;

/// <summary>
/// SEC-09: rate limits on the anonymous authentication endpoints. Without these,
/// `login` is an unthrottled password-guessing oracle and `send-otp` an unthrottled
/// SMS pump. Partitioning is by client IP because the caller is not authenticated
/// yet; a shared NAT therefore shares a bucket, which the limits allow for.
/// </summary>
public static class AuthRateLimitSetup
{
    /// <summary>Sign-in attempts: password and OTP.</summary>
    public const string SignIn = "AuthSignIn";

    /// <summary>Anything that causes an SMS to be sent.</summary>
    public const string OtpRequest = "AuthOtpRequest";

    public static IServiceCollection AddAuthRateLimits(
        this IServiceCollection services,
        IHostEnvironment environment)
    {
        // Local runs drive every account from one loopback address — the seeded demo
        // accounts, scripts/e2e-auth-onboarding.sh and a live demo all share a bucket —
        // so production limits would throttle the developer rather than an attacker.
        var relaxed = environment.IsDevelopment() || environment.IsEnvironment("Testing");

        services.AddRateLimiter(options =>
        {
            // Generous enough for a person mistyping a password, far below what a
            // credential-stuffing run needs. OtpService already enforces the 60s
            // resend cooldown and 5-attempt lock per phone (BR-61); this guards the
            // endpoint itself, which those per-phone rules cannot.
            options.AddPolicy(SignIn, PerClient(limit: relaxed ? 200 : 10, windowMinutes: 5));
            options.AddPolicy(OtpRequest, PerClient(limit: relaxed ? 200 : 20, windowMinutes: 15));

            options.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)retryAfter.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                await context.HttpContext.Response.WriteAsJsonAsync(
                    new { type = "rate_limited", title = "rate_limited", status = 429, detail = AppMessages.TooManyAttempts },
                    cancellationToken);
            };
        });

        return services;
    }

    private static Func<HttpContext, RateLimitPartition<string>> PerClient(int limit, int windowMinutes) =>
        context => RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limit,
                Window = TimeSpan.FromMinutes(windowMinutes),
                QueueLimit = 0,
            });
}
