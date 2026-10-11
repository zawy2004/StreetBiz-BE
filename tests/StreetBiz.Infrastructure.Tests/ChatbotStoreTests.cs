using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using StreetBiz.Application.Features.Chatbot;
using StreetBiz.Infrastructure.Persistence;
using StreetBiz.Infrastructure.Persistence.Repositories;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Tests;

public sealed class ChatbotStoreTests
{
    private static readonly ChatbotActor Actor = new(1, 7, "CUSTOMER", null, null);
    private static ChatbotSendRequest Request(string content = "Hướng dẫn đăng ký") => new(Guid.NewGuid().ToString("D"), content);

    [Fact]
    public async Task Create_is_idempotent_and_scoped()
    {
        using var f = await Fixture.Create();
        var key = Guid.NewGuid().ToString();
        var first = await f.Store.CreateAsync(Actor, key, default);
        Assert.Equal(first.Id, (await f.Store.CreateAsync(Actor, key, default)).Id);
        Assert.Equal(TimeSpan.FromHours(7), first.CreatedAt.Offset);
        await Assert.ThrowsAsync<ChatbotException>(() => f.Store.RequireAsync(Actor with { UserId = 2 }, first.Id, default));
        await Assert.ThrowsAsync<ChatbotException>(() => f.Store.RequireAsync(Actor with { Role = "PLATFORM_ADMIN" }, first.Id, default));
        await Assert.ThrowsAsync<ChatbotException>(() => f.Store.RequireAsync(Actor with { WardId = 3 }, first.Id, default));
    }

    [Fact]
    public async Task Completed_request_replays_but_changed_content_conflicts()
    {
        using var f = await Fixture.Create(); var id = await f.Conversation(); var request = Request();
        var start = await f.Store.StartAsync(Actor, id, request, default);
        var answer = start.Messages.AssistantMessage with { Content = "Hướng dẫn", Status = "COMPLETED", Version = 2 };
        await f.Store.SaveAsync(Actor, answer, "GROQ", "test-model", new(100, 50), default);
        var replay = await f.Store.StartAsync(Actor, id, request, default);
        Assert.True(replay.IsReplay); Assert.Equal(answer.Id, replay.Messages.AssistantMessage.Id);
        Assert.Equal(2, await f.Db.ChatbotMessages.CountAsync());
        Assert.Equal("request_reused", (await Assert.ThrowsAsync<ChatbotException>(() => f.Store.StartAsync(Actor, id, request with { Content = "khác" }, default))).Code);
        Assert.Equal("request_reused", (await Assert.ThrowsAsync<ChatbotException>(() => f.Store.StartAsync(Actor, id, request with { ResponseStyle = "steps" }, default))).Code);
    }

    [Fact]
    public async Task Image_marker_style_and_checklist_survive_reload_without_image_bytes()
    {
        using var f = await Fixture.Create(); var id = await f.Conversation();
        var start = await f.Store.StartAsync(Actor, id, Request() with { AttachmentIds = [Guid.NewGuid().ToString()], ResponseStyle = "steps" }, default);
        Assert.True(start.Messages.UserMessage.HasAttachments);
        await f.Store.SaveAsync(Actor, start.Messages.AssistantMessage with { Status = "COMPLETED", Version = 2, Checklist = [new("Mở hồ sơ")] }, "GEMINI", "test", new(40, 20), default);
        var saved = await f.Store.MessageAsync(Actor, id, start.Messages.AssistantMessage.Id, default);
        Assert.True(saved.HasAttachments); Assert.Equal("steps", saved.ResponseStyle);
        Assert.Equal("Mở hồ sơ", Assert.Single(saved.Checklist!).Text);
    }

    [Fact]
    public async Task Active_turn_prevents_duplicate_and_second_turn()
    {
        using var f = await Fixture.Create(); var id = await f.Conversation(); var request = Request();
        var start = await f.Store.StartAsync(Actor, id, request, default);
        foreach (var next in new[] { request, Request("câu khác") })
        {
            var error = await Assert.ThrowsAsync<ChatbotException>(() => f.Store.StartAsync(Actor, id, next, default));
            Assert.Equal(409, error.Status); Assert.Equal(start.Messages.AssistantMessage.Id, error.ActiveMessageId);
        }
    }

    [Fact]
    public async Task Cancellation_wins_over_late_completion_and_retains_budget()
    {
        using var f = await Fixture.Create(); var id = await f.Conversation();
        var start = await f.Store.StartAsync(Actor, id, Request(), default);
        var message = start.Messages.AssistantMessage;
        await f.Store.SaveAsync(Actor, message with { Content = "partial", Version = 2 }, "GROQ", "test", new(200, 20), default);
        await f.Store.CancelAsync(Actor, id, message.Id, default);
        await f.Store.SaveAsync(Actor, message with { Status = "COMPLETED", Version = 99, Content = "late" }, "GROQ", "test", new(300, 30), default);
        var saved = await f.Store.MessageAsync(Actor, id, message.Id, default);
        Assert.Equal("CANCELLED", saved.Status); Assert.Equal("partial", saved.Content);
        Assert.Null((await f.Store.RequireAsync(Actor, id, default)).ActiveMessageId);
        var row = await f.Db.ChatbotMessages.AsNoTracking().SingleAsync(m => m.message_id == message.Id);
        Assert.True(row.input_tokens >= f.Settings.MaxTurnTokenBudget); Assert.Equal(0, row.reserved_tokens);
    }

    [Fact]
    public async Task Expired_lease_recovers_and_does_not_refund_unknown_usage()
    {
        using var f = await Fixture.Create(); var id = await f.Conversation();
        var start = await f.Store.StartAsync(Actor, id, Request(), default);
        f.Clock.Now = f.Clock.Now.AddMinutes(3);
        var message = await f.Store.MessageAsync(Actor, id, start.Messages.AssistantMessage.Id, default);
        Assert.Equal("INTERRUPTED", message.Status); Assert.NotNull(message.CompletedAt);
        Assert.Null((await f.Store.RequireAsync(Actor, id, default)).ActiveMessageId);
        Assert.Equal(f.Settings.MaxTurnTokenBudget, await f.Db.ChatbotMessages.SumAsync(m => m.input_tokens));
    }

    [Fact]
    public async Task Delete_removes_text_but_cannot_reset_daily_limit()
    {
        using var f = await Fixture.Create(); f.Settings.DailyTokenBudget = f.Settings.MaxTurnTokenBudget;
        var id = await f.Conversation(); var start = await f.Store.StartAsync(Actor, id, Request("Nội dung riêng tư"), default);
        await f.Store.DeleteAsync(Actor, id, default);
        await Assert.ThrowsAsync<ChatbotException>(() => f.Store.HistoryAsync(Actor, id, null, default));
        Assert.All(await f.Db.ChatbotMessages.AsNoTracking().ToListAsync(), m => Assert.Equal("{}", m.payload_json));
        var newId = await f.Conversation();
        var error = await Assert.ThrowsAsync<ChatbotException>(() => f.Store.StartAsync(Actor, newId, Request(), default));
        Assert.Equal("daily_budget", error.Code);
    }

    [Fact]
    public async Task Demo_can_disable_daily_admission_without_erasing_usage()
    {
        using var f = await Fixture.Create(); f.Settings.EnforceDailyTokenBudget = false;
        f.Settings.DailyTokenBudget = 1;
        var id = await f.Conversation();
        var first = await f.Store.StartAsync(Actor, id, Request(), default);
        await f.Store.SaveAsync(Actor, first.Messages.AssistantMessage with { Status = "COMPLETED", Version = 2 }, "GROQ", "test", new(100, 20), default);
        var second = await f.Store.StartAsync(Actor, id, Request(), default);
        Assert.False(second.IsReplay);
        Assert.Equal(100, await f.Db.ChatbotMessages.Where(m => m.message_id == first.Messages.AssistantMessage.Id).Select(m => m.input_tokens).SingleAsync());
    }

    [Fact]
    public async Task Completed_usage_settles_an_inflight_estimate()
    {
        using var f = await Fixture.Create(); var id = await f.Conversation();
        var first = await f.Store.StartAsync(Actor, id, Request(), default);
        await f.Store.SaveAsync(Actor, first.Messages.AssistantMessage with { Version = 2 }, "GROQ", "test", new(9000, 2000), default);
        await f.Store.SaveAsync(Actor, first.Messages.AssistantMessage with { Status = "COMPLETED", Version = 3 }, "GROQ", "test", new(100, 20), default);
        var row = await f.Db.ChatbotMessages.AsNoTracking().SingleAsync(m => m.message_id == first.Messages.AssistantMessage.Id);
        Assert.Equal(100, row.input_tokens); Assert.Equal(20, row.output_tokens); Assert.Equal(0, row.reserved_tokens);
    }

    [Fact]
    public async Task Retention_excludes_history_and_feedback_requires_own_assistant()
    {
        using var f = await Fixture.Create(); var id = await f.Conversation(); var start = await f.Store.StartAsync(Actor, id, Request(), default);
        await Assert.ThrowsAsync<ChatbotException>(() => f.Store.FeedbackAsync(Actor, id, start.Messages.UserMessage.Id, true, null, default));
        await Assert.ThrowsAsync<ChatbotException>(() => f.Store.FeedbackAsync(Actor with { UserId = 2 }, id, start.Messages.AssistantMessage.Id, true, null, default));
        f.Clock.Now = f.Clock.Now.AddDays(31);
        Assert.Empty((await f.Store.ListAsync(Actor, null, default)).Items);
        await Assert.ThrowsAsync<ChatbotException>(() => f.Store.HistoryAsync(Actor, id, null, default));
    }

    [Fact]
    public async Task Conversations_are_chronological_not_random_guid_order()
    {
        using var f = await Fixture.Create(); var expected = new List<string>();
        for (var i = 0; i < 23; i++) { f.Clock.Now = f.Clock.Now.AddSeconds(1); expected.Insert(0, await f.Conversation()); }
        var page = await f.Store.ListAsync(Actor, null, default);
        var next = await f.Store.ListAsync(Actor, page.NextCursor, default);
        Assert.Equal(expected, page.Items.Concat(next.Items).Select(c => c.Id));
    }

    [Fact]
    public async Task Voice_seconds_are_summed_per_local_day_and_spoken_turns_reserve_no_chat_budget()
    {
        using var f = await Fixture.Create(); var id = await f.Conversation();
        await f.Store.RecordVoiceAsync(Actor, "s1", 120, "ended", default);
        await f.Store.RecordVoiceAsync(Actor, "s2", 45, "time_limit", default);
        await f.Store.RecordVoiceAsync(Actor with { UserId = 2 }, "s3", 300, "ended", default);
        Assert.Equal(165, await f.Store.VoiceSecondsTodayAsync(Actor.UserId, default));
        f.Clock.Now = f.Clock.Now.AddDays(1);
        Assert.Equal(0, await f.Store.VoiceSecondsTodayAsync(Actor.UserId, default));

        f.Settings.DailyTokenBudget = f.Settings.MaxTurnTokenBudget; f.Settings.EnforceDailyTokenBudget = true;
        var spoken = await f.Store.StartAsync(Actor, id, Request("Phí của tôi") with { Channel = "VOICE" }, default);
        Assert.Equal("VOICE", spoken.Messages.UserMessage.Channel);
        Assert.Equal(0, (await f.Db.ChatbotMessages.SingleAsync(m => m.message_id == spoken.Messages.AssistantMessage.Id)).reserved_tokens);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 7, 3, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Fixture : IDisposable
    {
        public readonly SqliteConnection Connection = new("Data Source=:memory:");
        public TestContext Db = null!;
        public readonly Clock Clock = new();
        public readonly ChatbotSettings Settings = new();
        public ChatbotStore Store => new(Db, Clock, Settings);
        public async Task<string> Conversation() => (await Store.CreateAsync(Actor, Guid.NewGuid().ToString(), default)).Id;
        public static async Task<Fixture> Create()
        {
            var f = new Fixture(); await f.Connection.OpenAsync();
            f.Db = new(new DbContextOptionsBuilder<StreetBizDbContext>().UseSqlite(f.Connection).Options);
            await f.Db.Database.EnsureCreatedAsync();
            f.Db.Roles.Add(new Role { role_code = "CUSTOMER", role_name = "Customer" });
            f.Db.UserAccounts.Add(new UserAccount { user_id = 1, phone_number = "0900000001", password_hash = "test-only", role_code = "CUSTOMER", account_status = "ACTIVE" });
            await f.Db.SaveChangesAsync(); return f;
        }
        public void Dispose() { Db.Dispose(); Connection.Dispose(); }
    }
    private sealed class TestContext(DbContextOptions<StreetBizDbContext> options) : StreetBizDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            foreach (var entity in builder.Model.GetEntityTypes()) foreach (var p in entity.GetProperties())
            { if (p.GetComputedColumnSql() != null) { p.SetComputedColumnSql(null); p.ValueGenerated = ValueGenerated.Never; } p.SetDefaultValueSql(null); }
        }
    }
}
