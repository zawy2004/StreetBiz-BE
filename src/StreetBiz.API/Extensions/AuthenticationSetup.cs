using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using StreetBiz.Application.Common.Interfaces;

namespace StreetBiz.API.Extensions;

public static class AuthenticationSetup
{
    public static IServiceCollection AddApiServices(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddControllers();
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, CurrentUser>();
        services.AddExceptionHandler<GlobalExceptionHandler>();

        var signingKey = configuration["Jwt:SigningKey"];
        if (string.IsNullOrWhiteSpace(signingKey))
        {
            throw new InvalidOperationException(
                "Jwt:SigningKey is not configured. Set it in configuration or user-secrets.");
        }

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = configuration["Jwt:Issuer"] ?? "StreetBiz",
                    ValidAudience = configuration["Jwt:Audience"] ?? "StreetBiz",
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
                    ClockSkew = TimeSpan.FromSeconds(30),
                };

                // A signature-valid JWT is not enough: sign-out (AUTH-04), revoking a device
                // (AUTH-09), changing and resetting the password all revoke the session row,
                // and without this check the access token would keep working until it expires.
                options.Events = new JwtBearerEvents { OnTokenValidated = ValidateSessionAsync };
            });

        services.AddAuthorization();
        return services;
    }

    /// <summary>How stale last_active_at may get before a request refreshes it.</summary>
    private static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(5);

    private static async Task ValidateSessionAsync(TokenValidatedContext context)
    {
        var sid = context.Principal?.FindFirst("sid")?.Value;
        if (!long.TryParse(sid, out var sessionId))
        {
            context.Fail("The access token has no session.");
            return;
        }

        var services = context.HttpContext.RequestServices;
        var sessions = services.GetRequiredService<ISessionRepository>();
        var clock = services.GetRequiredService<IDateTimeProvider>();
        var cancellationToken = context.HttpContext.RequestAborted;

        var session = await sessions.GetByIdAsync(sessionId, cancellationToken);
        var now = clock.UtcNow;
        if (session is null || session.RevokedAt is not null || session.ExpiresAt <= now)
        {
            context.Fail("The session has been signed out.");
            return;
        }

        // Keeps "last active" on the sessions screen meaningful (AUTH-08) without
        // writing to the database on every single request.
        if (session.LastActiveAt is null || now - session.LastActiveAt.Value > TouchInterval)
        {
            await sessions.TouchAsync(sessionId, cancellationToken);
        }
    }
}
