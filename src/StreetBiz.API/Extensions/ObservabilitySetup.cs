using Azure.Monitor.OpenTelemetry.AspNetCore;

namespace StreetBiz.API.Extensions;

/// <summary>
/// Application Insights through OpenTelemetry, switched on only by a connection string.
/// <para>
/// Set <c>APPLICATIONINSIGHTS_CONNECTION_STRING</c> (in <c>api.env</c> on the VPS; it works from any host)
/// to send requests, dependencies (SQL, HTTP calls to Gemini/payOS/eSMS) and failures to Azure Monitor.
/// Without it nothing is registered, so local runs, tests and a deployment that has not enabled it
/// behave exactly as before.
/// </para>
/// <para>
/// Logs keep going to Serilog only (Serilog replaces the logging providers), so nothing logged is sent
/// to Azure twice and log content stays where it is today. Query string values are redacted by the
/// ASP.NET Core instrumentation by default.
/// </para>
/// </summary>
public static class ObservabilitySetup
{
    public const string ConnectionStringKey = "APPLICATIONINSIGHTS_CONNECTION_STRING";

    /// <summary>Optional share of traces kept, 0 to 1 (default 1); set to lower the ingestion bill.</summary>
    public const string SamplingRatioKey = "AzureMonitor:SamplingRatio";

    /// <returns>True when Azure Monitor was registered.</returns>
    public static bool AddAzureMonitorIfConfigured(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration[ConnectionStringKey];
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return false;
        }

        var samplingRatio = configuration.GetValue<float?>(SamplingRatioKey) ?? 1f;
        if (samplingRatio is < 0f or > 1f)
        {
            throw new InvalidOperationException($"{SamplingRatioKey} must be between 0 and 1.");
        }

        services.AddOpenTelemetry().UseAzureMonitor(options =>
        {
            options.ConnectionString = connectionString;
            options.SamplingRatio = samplingRatio;
        });
        return true;
    }
}
