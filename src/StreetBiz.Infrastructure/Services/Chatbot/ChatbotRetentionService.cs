using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StreetBiz.Application.Features.Chatbot;
using StreetBiz.Infrastructure.Persistence;

namespace StreetBiz.Infrastructure.Services.Chatbot;

/// <summary>
/// Deletes chatbot conversations past <see cref="ChatbotSettings.RetentionDays"/> (and the ones a user
/// deleted, once the day is over) plus expired audit rows. Small batches; no startup migration or
/// database recreation. Audit retention contains no message text.
/// <para>
/// Runs every <see cref="ChatbotSettings.RetentionSweepMinutes"/> (6 hours by default). It used to run
/// every 15 minutes, which woke a serverless (auto-pausing) database four times an hour so it never paused.
/// Each run now keeps deleting batch after batch until nothing is left, so a longer interval does not
/// leave a backlog behind.
/// </para>
/// </summary>
public sealed class ChatbotRetentionService(IServiceScopeFactory scopes, ChatbotSettings settings,
    TimeProvider clock, ILogger<ChatbotRetentionService> logger) : BackgroundService
{
    public const int ConversationBatch = 100;
    public const int AuditBatch = 1000;

    /// <summary>Upper bound on batches per run, so one run cannot hold the database for long.</summary>
    public const int MaxBatchesPerRun = 50;

    /// <summary>
    /// First run this long after start-up. A restart (every deploy, every platform recycle) resets the
    /// periodic timer, so without it an app restarted more often than the interval would never sweep.
    /// The delay keeps the sweep out of the start-up rush.
    /// </summary>
    public static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.Enabled) return;
        try
        {
            await Task.Delay(StartupDelay, clock, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await RunOnceAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(settings.RetentionSweepMinutes), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunOnceAsync(stoppingToken);
        }
    }

    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<StreetBizDbContext>();
            var removed = await SweepAsync(db, clock.GetUtcNow(), settings.RetentionDays, stoppingToken);
            if (removed.Conversations > 0 || removed.Audits > 0)
            {
                logger.LogInformation("Chatbot retention removed {Conversations} conversation(s) and {Audits} audit row(s).",
                    removed.Conversations, removed.Audits);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { logger.LogWarning("Chatbot retention deferred ({ErrorType}). Verify schema deployment.", ex.GetType().Name); }
    }

    /// <summary>One retention run: conversations then audits, each in batches until none are left.</summary>
    public static async Task<(int Conversations, int Audits)> SweepAsync(
        StreetBizDbContext db, DateTimeOffset now, int retentionDays, CancellationToken cancellationToken)
    {
        var cutoff = now.UtcDateTime.AddDays(-retentionDays);
        var dayStart = now.ToOffset(TimeSpan.FromHours(7)).Date.AddHours(-7);

        var conversations = 0;
        for (var batch = 0; batch < MaxBatchesPerRun; batch++)
        {
            var ids = await db.ChatbotConversations
                .Where(c => c.updated_at < cutoff || (c.deleted_at != null && c.updated_at < dayStart))
                .OrderBy(c => c.updated_at).Select(c => c.conversation_id).Take(ConversationBatch)
                .ToArrayAsync(cancellationToken);
            if (ids.Length == 0) break;
            conversations += await db.ChatbotConversations.Where(c => ids.Contains(c.conversation_id))
                .ExecuteDeleteAsync(cancellationToken);
            if (ids.Length < ConversationBatch) break;
        }

        var audits = 0;
        for (var batch = 0; batch < MaxBatchesPerRun; batch++)
        {
            var ids = await db.ChatbotAudits.Where(a => a.timestamp < cutoff)
                .OrderBy(a => a.audit_id).Select(a => a.audit_id).Take(AuditBatch)
                .ToArrayAsync(cancellationToken);
            if (ids.Length == 0) break;
            audits += await db.ChatbotAudits.Where(a => ids.Contains(a.audit_id))
                .ExecuteDeleteAsync(cancellationToken);
            if (ids.Length < AuditBatch) break;
        }

        return (conversations, audits);
    }
}
