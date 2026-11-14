using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StreetBiz.Application.Features.AiMarketplace;

namespace StreetBiz.Infrastructure.Services;

/// <summary>
/// Ai:Enabled switches every AI feature at once; Ai:Features:{code} (for example Ai:Features:AIP-03) switches one.
/// Both default to on, because every feature has a non-AI fallback and costs nothing while no key is set.
/// </summary>
public sealed class AiFeatureGate(IConfiguration configuration) : IAiFeatureGate
{
    public bool IsEnabled(string featureCode) =>
        configuration.GetValue("Ai:Enabled", true) && configuration.GetValue($"Ai:Features:{featureCode}", true);
}

/// <summary>
/// Gives the marketplace and report-assist features the one model call they need. It adapts the shared Gemini
/// client (key and model failover, JSON reply) and never throws: any failure is "no answer", and the feature falls back.
/// </summary>
public sealed class GeminiSuggestionClient(GeminiVisionClient gemini, ILogger<GeminiSuggestionClient> logger) : IAiSuggestionClient
{
    public async Task<AiGeneration> GenerateJsonAsync(
        string prompt,
        IReadOnlyList<AiImage> images,
        CancellationToken cancellationToken)
    {
        try
        {
            var (success, content) = await gemini.CallAsync(
                prompt, images.Select(image => (image.Bytes, image.MimeType)).ToArray(), cancellationToken);
            return new AiGeneration(success, content);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new AiGeneration(false, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "AI suggestion call failed: {Type}", ex.GetType().Name);
            return new AiGeneration(false, null);
        }
    }
}

/// <summary>
/// AIB-01 in the background: when a citizen files a report with a photo, the photo is read once so the ward
/// officer finds the notes already there. The request that filed the report never waits for it.
/// </summary>
public sealed class VendorReportPreprocessor(
    IServiceScopeFactory scopes,
    ILogger<VendorReportPreprocessor> logger) : BackgroundService, IVendorReportPreprocessor
{
    private readonly Channel<long> queue = Channel.CreateBounded<long>(
        new BoundedChannelOptions(200) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Enqueue(long reportId) => queue.Writer.TryWrite(reportId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var reportId in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var repository = scope.ServiceProvider.GetRequiredService<IAiMarketplaceRepository>();
                var report = await repository.GetVendorReportAsync(reportId, stoppingToken);
                if (report is not null)
                {
                    await scope.ServiceProvider.GetRequiredService<IVendorReportAssistant>()
                        .RunAsync(report, force: false, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // The officer can still ask for the analysis by hand.
                logger.LogWarning(ex, "AIB-01 pre-processing of report {ReportId} failed.", reportId);
            }
        }
    }
}
