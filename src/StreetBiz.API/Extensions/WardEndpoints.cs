using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using StreetBiz.Application.Features.WardSlots;

namespace StreetBiz.API.Extensions;

public static class WardEndpoints
{
    public static IServiceCollection AddWardApi(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("WardGeo", context =>
                System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                    context.User.FindFirstValue("sub")
                    ?? context.Connection.RemoteIpAddress?.ToString()
                    ?? "anonymous",
                    _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 30,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    }));
            options.AddPolicy("WardDevSession", context =>
                System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
                    _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 5,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    }));
        });
        return services;
    }

    /// <summary>
    /// Maps only the local bootstrap route. Production Ward routes are regular
    /// controller actions and share the application's JWT/session pipeline.
    /// </summary>
    public static void MapWardApi(this WebApplication app)
    {
        if (!(app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing")))
        {
            return;
        }

        app.MapPost("/api/dev/ward-session", async (
            DevSession request,
            IConfiguration configuration,
            IWardActorResolver resolver,
            CancellationToken cancellationToken) =>
        {
            var developmentKey = configuration["WardDevelopment:AccessKey"];
            var signingKey = configuration["Jwt:SigningKey"];
            if (developmentKey is not { Length: >= 32 }
                || signingKey is not { Length: >= 32 }
                || !long.TryParse(configuration["WardDevelopment:UserId"], out var userId))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Chưa cấu hình phiên phát triển trên Backend.");
            }

            if (string.IsNullOrWhiteSpace(request.AccessKey)
                || !CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(Encoding.UTF8.GetBytes(request.AccessKey)),
                    SHA256.HashData(Encoding.UTF8.GetBytes(developmentKey))))
            {
                return Results.Unauthorized();
            }

            var actor = await resolver.ResolveAsync(userId, cancellationToken);
            if (actor is null)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "UserId cấu hình phải là cán bộ phường ACTIVE có ward_unit_id.");
            }

            var expiresAt = DateTime.UtcNow.AddMinutes(30);
            var token = new JwtSecurityToken(
                configuration["Jwt:Issuer"] ?? "StreetBiz",
                configuration["Jwt:Audience"] ?? "StreetBiz",
                [
                    new Claim("sub", actor.UserId.ToString()),
                    new Claim("ward_dev", "true"),
                    new Claim("role", "WARD_AUTHORITY"),
                    new Claim("name", actor.Name),
                ],
                DateTime.UtcNow,
                expiresAt,
                new SigningCredentials(
                    new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
                    SecurityAlgorithms.HmacSha256));

            return Results.Ok(new
            {
                accessToken = new JwtSecurityTokenHandler().WriteToken(token),
                expiresAt,
            });
        })
        .AllowAnonymous()
        .RequireRateLimiting("WardDevSession")
        .WithTags("Development");
    }

    public sealed record DevSession(string AccessKey);
}
