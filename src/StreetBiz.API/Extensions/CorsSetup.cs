namespace StreetBiz.API.Extensions;

/// <summary>
/// Browser CORS policy for the StreetBiz-FE single-page app.
/// Origins come from <c>Cors:AllowedOrigins</c> so each environment can set its own.
/// </summary>
public static class CorsSetup
{
    public const string PolicyName = "StreetBizFrontend";

    private static readonly string[] FallbackOrigins =
    [
        "http://localhost:5173",  // vite dev server
        "http://127.0.0.1:5173",
        "http://localhost:4173",  // vite preview
    ];

    public static IServiceCollection AddStreetBizCors(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var origins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>();
        if (origins is null || origins.Length == 0)
        {
            origins = FallbackOrigins;
        }

        services.AddCors(options => options.AddPolicy(PolicyName, policy =>
        {
            if (environment.IsDevelopment())
            {
                // Vite silently moves to 5174, 5175, ... when 5173 is taken, so a fixed
                // list breaks local runs. In development accept any loopback port.
                policy.SetIsOriginAllowed(origin =>
                    origins.Contains(origin, StringComparer.OrdinalIgnoreCase) || IsLoopback(origin));
            }
            else
            {
                policy.WithOrigins(origins);
            }

            policy
                .AllowAnyHeader()
                .AllowAnyMethod()
                // The SPA sends the refresh token in the body, not a cookie, so credentials
                // are not required; keeping them off allows a stricter origin check.
                .WithExposedHeaders("Retry-After", "WWW-Authenticate");
        }));

        return services;
    }

    private static bool IsLoopback(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && uri.IsLoopback;
}
