using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Infrastructure.Persistence;

namespace StreetBiz.Infrastructure.Storage;

public sealed class OrphanImageCleanupOptions
{
    public const string SectionName = "OrphanImageCleanup";

    public bool Enabled { get; set; } = true;

    /// <summary>How often the sweep runs.</summary>
    public int IntervalMinutes { get; set; } = 360;

    /// <summary>
    /// A photo is only a candidate once it is this old: a vendor uploads it before saving the
    /// dish or the store, and may leave the form open for a while.
    /// </summary>
    public int GraceHours { get; set; } = 24;
}

/// <summary>
/// Deletes public photos nothing points at any more: a dish photo replaced by a new one, an upload
/// whose form was never saved, a removed avatar. Only the public folders (<c>menu/</c>,
/// <c>avatars/</c>) are swept. Evidence, slot photos and receipts are never touched. A file is kept
/// when its name appears anywhere it can be referenced: the dish, store and account columns,
/// chatbot replies (which show dish photos) and the audit log.
/// </summary>
public sealed class OrphanImageSweeper(StreetBizDbContext db, IFileStorage storage, TimeProvider clock)
{
    public static readonly string[] Folders = ["menu", "avatars"];

    public async Task<int> SweepAsync(TimeSpan grace, CancellationToken cancellationToken)
    {
        var cutoff = clock.GetUtcNow().UtcDateTime - grace;
        var candidates = new List<(string Path, string Url)>();
        foreach (var folder in Folders)
        {
            foreach (var file in await storage.ListAsync(folder, cancellationToken))
            {
                if (file.LastWriteUtc <= cutoff && UrlOf(file.RelativePath) is { } url)
                {
                    candidates.Add((file.RelativePath, url));
                }
            }
        }

        if (candidates.Count == 0)
        {
            return 0;
        }

        var referenced = new HashSet<string>(StringComparer.Ordinal);
        referenced.UnionWith(await db.MenuItems.Where(x => x.image_url != null).Select(x => x.image_url!).ToListAsync(cancellationToken));
        referenced.UnionWith(await db.Storefronts.Where(x => x.image_url != null).Select(x => x.image_url!).ToListAsync(cancellationToken));
        referenced.UnionWith(await db.UserAccounts.Where(x => x.avatar_url != null).Select(x => x.avatar_url!).ToListAsync(cancellationToken));

        var deleted = 0;
        foreach (var (path, url) in candidates)
        {
            if (referenced.Contains(url))
            {
                continue;
            }

            // The random 32-hex name is unique, so a substring match on it is exact enough.
            var name = Path.GetFileName(path);
            if (await db.ChatbotMessages.AnyAsync(x => x.payload_json.Contains(name), cancellationToken)
                || await db.AuditLogs.AnyAsync(x => x.details != null && x.details.Contains(name), cancellationToken))
            {
                continue;
            }

            await storage.DeleteAsync(path, cancellationToken);
            deleted++;
        }

        return deleted;
    }

    /// <summary>The public URL a stored file is served at, or null for anything unexpected.</summary>
    private static string? UrlOf(string relativePath)
    {
        var parts = relativePath.Split('/');
        if (parts.Length != 3 || !long.TryParse(parts[1], out var owner))
        {
            return null;
        }

        return parts[0] switch
        {
            "menu" when MenuImageFiles.IsValidFileName(parts[2]) => MenuImageFiles.BuildUrl(owner, parts[2]),
            "avatars" when AvatarFiles.IsValidFileName(parts[2]) => AvatarFiles.BuildUrl(owner, parts[2]),
            _ => null,
        };
    }
}

/// <summary>Runs <see cref="OrphanImageSweeper"/> on a timer, first after ten minutes so startup stays quick.</summary>
public sealed class OrphanImageCleanupService(
    IServiceScopeFactory scopeFactory,
    IOptions<OrphanImageCleanupOptions> options,
    TimeProvider clock,
    ILogger<OrphanImageCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromMinutes(10), clock, stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(5, settings.IntervalMinutes)), clock);
            do
            {
                await RunOnceAsync(TimeSpan.FromHours(Math.Max(1, settings.GraceHours)), stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunOnceAsync(TimeSpan grace, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var deleted = await scope.ServiceProvider.GetRequiredService<OrphanImageSweeper>()
                .SweepAsync(grace, cancellationToken);
            if (deleted > 0)
            {
                logger.LogInformation("Orphan image cleanup deleted {Count} unused photo(s).", deleted);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Orphan image cleanup failed; it will run again on the next tick.");
        }
    }
}
