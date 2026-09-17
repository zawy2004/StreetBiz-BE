using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.OpenApi.Models;
using Serilog;
using StreetBiz.API.Extensions;
using StreetBiz.Application;
using StreetBiz.Infrastructure;
using StreetBiz.Infrastructure.Storage;

var builder = WebApplication.CreateBuilder(args);

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
builder.Services
    .AddHealthChecks()
    .AddSqlServer(
        databaseConnectionString,
        name: "sqlserver",
        failureStatus: HealthStatus.Unhealthy,
        tags: ["database", "ready"]);

var app = builder.Build();

app.UseExceptionHandler();
app.UseSerilogRequestLogging();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Redirecting to HTTPS in development would turn the SPA's plain-HTTP calls into
// 307s, which browsers refuse to follow for a CORS preflight. Keep the redirect
// to deployed environments, which are served over HTTPS end to end.
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseCors(CorsSetup.PolicyName);

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.MapHealthChecks(
    "/health",
    new HealthCheckOptions
    {
        Predicate = healthCheck => healthCheck.Tags.Contains("ready")
    });

app.Run();

public partial class Program;
