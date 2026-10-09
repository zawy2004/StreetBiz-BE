using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StreetBiz.Application.Features.Chatbot;
using StreetBiz.Infrastructure.Persistence;

namespace StreetBiz.Infrastructure.Services.Chatbot;

/// <summary>Small batches; no startup migration or database recreation. Audit retention contains no message text.</summary>
public sealed class ChatbotRetentionService(IServiceScopeFactory scopes, ChatbotSettings settings,
    TimeProvider clock, ILogger<ChatbotRetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.Enabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<StreetBizDbContext>();
                var cutoff = clock.GetUtcNow().UtcDateTime.AddDays(-settings.RetentionDays);
                var dayStart = clock.GetUtcNow().ToOffset(TimeSpan.FromHours(7)).Date.AddHours(-7);
                var ids = await db.ChatbotConversations.Where(c => c.updated_at < cutoff || (c.deleted_at != null && c.updated_at < dayStart))
                    .OrderBy(c => c.updated_at).Select(c => c.conversation_id).Take(100).ToArrayAsync(stoppingToken);
                await db.ChatbotConversations.Where(c => ids.Contains(c.conversation_id)).ExecuteDeleteAsync(stoppingToken);
                var auditIds = await db.ChatbotAudits.Where(a => a.timestamp < cutoff).OrderBy(a => a.audit_id).Select(a => a.audit_id).Take(1000).ToArrayAsync(stoppingToken);
                await db.ChatbotAudits.Where(a => auditIds.Contains(a.audit_id)).ExecuteDeleteAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { logger.LogWarning("Chatbot retention deferred ({ErrorType}). Verify schema deployment.", ex.GetType().Name); }
        }
    }
}
