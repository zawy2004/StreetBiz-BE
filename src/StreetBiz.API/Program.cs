using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.OpenApi.Models;
using Serilog;
using System.Security.Claims;
using StreetBiz.API.Extensions;
using StreetBiz.Application;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Infrastructure;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Storage;
using StreetBiz.API.Hubs;

// `--sign-demo-permits` is a one-off development command (see DemoPermits), not configuration.
var signDemoPermits = args.Contains(DemoPermits.CommandLineSwitch);
var builder = WebApplication.CreateBuilder(args.Where(arg => arg != DemoPermits.CommandLineSwitch).ToArray());

builder.Host.UseSerilog((context, services, configuration) =>
{
    configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services);
});

var databaseConnectionString =
    builder.Configuration.GetStreetBizDatabaseConnectionString();

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
// Holidays and làm-bù Saturdays announced after this release; VietnamPublicHolidays has the rest.
BusinessCalendar.Configure(
    builder.Configuration.GetSection("BusinessCalendar:ExtraHolidays").Get<DateOnly[]>() ?? [],
    builder.Configuration.GetSection("BusinessCalendar:ExtraWorkingDays").Get<DateOnly[]>() ?? []);
// Resolve a relative upload root against the project, not the bin folder, so
// uploads survive a rebuild. An absolute Storage:RootPath is used as-is.
builder.Services.PostConfigure<StorageSettings>(settings =>
    settings.RootPath = Path.Combine(builder.Environment.ContentRootPath, settings.RootPath));
builder.Services.AddApiServices(builder.Configuration);
builder.Services.AddStreetBizCors(builder.Configuration, builder.Environment);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    // Lets "Authorize" in Swagger UI send the access token from /api/auth/login.
    var bearer = new OpenApiSecurityScheme
    {
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Description = "Paste the accessToken returned by /api/auth/login.",
        Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" },
    };
    options.AddSecurityDefinition("Bearer", bearer);
    options.AddSecurityRequirement(new OpenApiSecurityRequirement { [bearer] = [] });
});
builder.Services.AddProblemDetails();
builder.Services.AddWardApi();
builder.Services.AddCommunityApi();
builder.Services.AddAuthRateLimits(builder.Environment);
builder.Services.AddChatbotApi();
builder.Services.AddAiRateLimit();
// ai/vendor-assistant calls a paid LLM per request; an earlier draft left it [AllowAnonymous]
// with no limit. Now [Authorize]-only, plus this per-account cap.
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("VendorAssistantAi", context =>
        System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            context.User.FindFirstValue("sub")
            ?? context.Connection.RemoteIpAddress?.ToString()
            ?? "anonymous",
            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = 15,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

    // REG-02 eKYC: each call spends FPT.AI credits on a paid quota, and a legitimate
    // applicant only ever needs a handful of scans (front, back, portrait, plus retries).
    options.AddPolicy("VendorKycAi", context =>
        System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            context.User.FindFirstValue("sub")
            ?? context.Connection.RemoteIpAddress?.ToString()
            ?? "anonymous",
            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

    // X01 voice registration: each turn is one speech-to-text call plus one extraction call. A real
    // interview is 5-10 turns; this leaves room for retries without letting a loop burn the quota.
    options.AddPolicy("VendorVoiceAi", context =>
        System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            context.User.FindFirstValue("sub")
            ?? context.Connection.RemoteIpAddress?.ToString()
            ?? "anonymous",
            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(10),
                QueueLimit = 0,
            }));

    // ORD-06: the typed fallback is the one pickup path where the caller supplies
    // a short secret rather than a signed one. 40 bits is far too wide to guess,
    // but a cap turns "far too wide" into "not worth attempting".
    options.AddPolicy("PickupCode", context =>
        System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            context.User.FindFirstValue("sub")
            ?? context.Connection.RemoteIpAddress?.ToString()
            ?? "anonymous",
            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

    // CHAT-02: the only endpoint where one account writes rows straight into
    // another account's inbox, so it is the one worth capping. The limit is set
    // well above real typing - a person sends a handful of lines a minute, a
    // script sends thousands.
    options.AddPolicy("ChatSend", context =>
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
});
builder.Services.AddSingleton<IOrderRealtimePublisher, OrderRealtimePublisher>();
builder.Services.AddSingleton<IChatRealtimePublisher, ChatRealtimePublisher>();
builder.Services
    .AddHealthChecks()
    .AddSqlServer(
        databaseConnectionString,
        name: "sqlserver",
        failureStatus: HealthStatus.Unhealthy,
        tags: ["database", "ready"]);

var app = builder.Build();

if (signDemoPermits)
{
    // Run by scripts/setup-local-db.ps1 right after the demo seed: sign its placeholder permits with
    // this machine's Permit:SigningKey, then exit without serving anything.
    if (!app.Environment.IsDevelopment())
    {
        Console.Error.WriteLine($"{DemoPermits.CommandLineSwitch} only runs in Development.");
        Environment.ExitCode = 1;
        return;
    }

    using var scope = app.Services.CreateScope();
    var signed = await DemoPermits.SignPlaceholdersAsync(
        scope.ServiceProvider.GetRequiredService<StreetBizDbContext>(),
        scope.ServiceProvider.GetRequiredService<IPermitTokenService>(),
        CancellationToken.None);
    Console.WriteLine($"Signed {signed} demo permit(s) with this machine's Permit:SigningKey.");
    return;
}

// Request logging wraps the exception handler so it records the status the client
// actually received (a validation failure is a 400, not a 500).
app.UseSerilogRequestLogging();
app.UseExceptionHandler();
app.UseMiddleware<ChatbotEnvelopeMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Redirecting a development SPA's CORS preflight from HTTP to HTTPS makes the
// browser reject it. Deployed environments are HTTPS end to end.
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

// Behind a reverse proxy every client shares the proxy IP, so the per-IP auth limits would
// become one global bucket. Only trust X-Forwarded-For from proxies listed in config.
if (app.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() is { Length: > 0 } proxies)
{
    var forwarded = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto };
    forwarded.KnownProxies.Clear();
    forwarded.KnownNetworks.Clear();
    foreach (var proxy in proxies)
    {
        forwarded.KnownProxies.Add(System.Net.IPAddress.Parse(proxy));
    }

    app.UseForwardedHeaders(forwarded);
}

app.UseCors(CorsSetup.PolicyName);
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
// SignalR adds this for its own hubs; the chatbot voice relay is a plain WebSocket endpoint.
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

app.MapControllers();
app.MapChatbotApi();
app.MapRegistrationVoiceApi();
app.MapWardApi();
app.MapFinanceDevApi();
app.MapHub<OrderHub>("/hubs/orders");
app.MapHub<ChatHub>("/hubs/chat");

app.MapHealthChecks(
    "/health",
    new HealthCheckOptions
    {
        Predicate = healthCheck => healthCheck.Tags.Contains("ready")
    });

app.Run();

public partial class Program;
