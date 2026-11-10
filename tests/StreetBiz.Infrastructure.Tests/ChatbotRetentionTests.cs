using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using StreetBiz.Application.Features.Chatbot;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;
using StreetBiz.Infrastructure.Services.Chatbot;

namespace StreetBiz.Infrastructure.Tests;

/// <summary>
/// The retention run now happens every few hours instead of every 15 minutes, so one run has to
/// clear everything that expired since the last one, not just the first batch.
/// </summary>
public sealed class ChatbotRetentionTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 5, 0, 0, TimeSpan.Zero); // 12:00 in Vietnam

    private SqliteConnection connection = null!;
    private TestContext db = null!;

    public async Task InitializeAsync()
    {
        connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        db = new TestContext(new DbContextOptionsBuilder<StreetBizDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.Roles.Add(new Role { role_code = "CUSTOMER", role_name = "Customer" });
        db.UserAccounts.Add(new UserAccount { user_id = 1, phone_number = "0900000001", password_hash = "test-only", role_code = "CUSTOMER", account_status = "ACTIVE" });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await db.DisposeAsync();
        await connection.DisposeAsync();
    }

    private void AddConversation(string id, DateTime updatedAt, DateTime? deletedAt = null) =>
        db.ChatbotConversations.Add(new ChatbotConversationRecord
        {
            conversation_id = id,
            owner_user_id = 1,
            actor_id = 1,
            create_request_id = id,
            scope = "CUSTOMER",
            created_at = updatedAt,
            updated_at = updatedAt,
            deleted_at = deletedAt,
        });

    [Fact]
    public async Task One_run_clears_a_backlog_larger_than_a_batch()
    {
        var old = Now.UtcDateTime.AddDays(-31);
        for (var i = 0; i < ChatbotRetentionService.ConversationBatch * 2 + 37; i++)
        {
            AddConversation($"old-{i:000}", old.AddMinutes(i));
        }

        AddConversation("fresh", Now.UtcDateTime.AddDays(-2));
        await db.SaveChangesAsync();

        var removed = await ChatbotRetentionService.SweepAsync(db, Now, retentionDays: 30, default);

        Assert.Equal(ChatbotRetentionService.ConversationBatch * 2 + 37, removed.Conversations);
        Assert.Equal(new[] { "fresh" }, await db.ChatbotConversations.Select(c => c.conversation_id).ToArrayAsync());
    }

    [Fact]
    public async Task A_conversation_the_user_deleted_goes_once_its_day_is_over_not_before()
    {
        var yesterday = Now.UtcDateTime.AddDays(-1);
        AddConversation("deleted-yesterday", yesterday, deletedAt: yesterday);
        AddConversation("deleted-today", Now.UtcDateTime.AddHours(-1), deletedAt: Now.UtcDateTime.AddHours(-1));
        AddConversation("kept", Now.UtcDateTime.AddDays(-29));
        await db.SaveChangesAsync();

        var removed = await ChatbotRetentionService.SweepAsync(db, Now, retentionDays: 30, default);

        Assert.Equal(1, removed.Conversations);
        Assert.Equal(new[] { "deleted-today", "kept" },
            await db.ChatbotConversations.OrderBy(c => c.conversation_id).Select(c => c.conversation_id).ToArrayAsync());
    }

    [Fact]
    public async Task Expired_audit_rows_go_and_recent_ones_stay()
    {
        for (var i = 1; i <= 5; i++)
        {
            db.ChatbotAudits.Add(new ChatbotAuditRecord
            {
                audit_id = i,
                message_id = $"m{i}",
                operation = "TURN",
                outcome = "OK",
                actor_id = 1,
                timestamp = i <= 3 ? Now.UtcDateTime.AddDays(-40) : Now.UtcDateTime.AddDays(-1),
            });
        }

        await db.SaveChangesAsync();

        var removed = await ChatbotRetentionService.SweepAsync(db, Now, retentionDays: 30, default);

        Assert.Equal(3, removed.Audits);
        Assert.Equal(new long[] { 4, 5 }, await db.ChatbotAudits.OrderBy(a => a.audit_id).Select(a => a.audit_id).ToArrayAsync());
    }

    [Fact]
    public async Task Nothing_expired_means_nothing_removed()
    {
        AddConversation("recent", Now.UtcDateTime.AddDays(-1));
        await db.SaveChangesAsync();

        var removed = await ChatbotRetentionService.SweepAsync(db, Now, retentionDays: 30, default);

        Assert.Equal((0, 0), removed);
    }

    [Fact]
    public async Task The_first_sweep_runs_shortly_after_start_up_not_one_full_interval_later()
    {
        // A restart resets the periodic timer; without the start-up run an app that restarts more
        // often than every 6 hours would never sweep.
        AddConversation("expired", Now.UtcDateTime.AddDays(-31));
        await db.SaveChangesAsync();

        var options = new DbContextOptionsBuilder<StreetBizDbContext>().UseSqlite(connection).Options;
        var services = new ServiceCollection();
        services.AddScoped<StreetBizDbContext>(_ => new TestContext(options));
        await using var provider = services.BuildServiceProvider();
        var clock = new FakeTimeProvider(Now);
        var service = new ChatbotRetentionService(provider.GetRequiredService<IServiceScopeFactory>(),
            new ChatbotSettings { RetentionSweepMinutes = 360 }, clock, NullLogger<ChatbotRetentionService>.Instance);

        await service.StartAsync(default);
        var waited = TimeSpan.Zero;
        try
        {
            // Step the clock a minute at a time until the expired conversation is gone.
            while (await CountAsync() > 0 && waited < TimeSpan.FromMinutes(30))
            {
                await Task.Delay(50);
                clock.Advance(TimeSpan.FromMinutes(1));
                waited += TimeSpan.FromMinutes(1);
                for (var i = 0; i < 10 && await CountAsync() > 0; i++) await Task.Delay(20);
            }
        }
        finally
        {
            await service.StopAsync(default);
        }

        Assert.Equal(0, await CountAsync());
        Assert.InRange(waited, ChatbotRetentionService.StartupDelay, ChatbotRetentionService.StartupDelay * 2);

        async Task<int> CountAsync()
        {
            await using var check = new TestContext(options);
            return await check.ChatbotConversations.CountAsync();
        }
    }

    private sealed class TestContext(DbContextOptions<StreetBizDbContext> options) : StreetBizDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            foreach (var entity in builder.Model.GetEntityTypes())
                foreach (var p in entity.GetProperties())
                {
                    if (p.GetComputedColumnSql() != null)
                    {
                        p.SetComputedColumnSql(null);
                        p.ValueGenerated = ValueGenerated.Never;
                    }

                    p.SetDefaultValueSql(null);
                }
        }
    }
}
