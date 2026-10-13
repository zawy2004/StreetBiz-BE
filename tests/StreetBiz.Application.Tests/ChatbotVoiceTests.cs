using System.Text.Json;
using System.Threading.Channels;
using MediatR;
using Moq;
using StreetBiz.Application.DTOs.Finance;
using StreetBiz.Application.Features.Chatbot;
using StreetBiz.Application.Features.Finance.GetSummary;

namespace StreetBiz.Application.Tests;

public sealed class ChatbotVoiceTests
{
    private static readonly ChatbotActor Vendor = new(1, 2, "VENDOR", null, 4);

    [Fact]
    public void Tickets_are_single_use_even_when_the_secret_is_wrong()
    {
        var sessions = new ChatbotVoiceSessions(new(), TimeProvider.System);
        var ticket = sessions.Issue(Vendor, "conversation", null, false, 0);
        Assert.Null(sessions.Redeem(ticket.SessionId, "guess"));
        Assert.Null(sessions.Redeem(ticket.SessionId, ticket.Secret));
        var second = sessions.Issue(Vendor, "conversation", null, false, 0);
        Assert.Equal(second, sessions.Redeem(second.SessionId, second.Secret));
        Assert.Null(sessions.Redeem(second.SessionId, second.Secret));
    }

    [Fact]
    public void Expired_ticket_is_rejected()
    {
        var clock = new Clock();
        var sessions = new ChatbotVoiceSessions(new() { TicketSeconds = 5 }, clock);
        var ticket = sessions.Issue(Vendor, "conversation", null, false, 0);
        clock.Now = clock.Now.AddSeconds(6);
        Assert.Null(sessions.Redeem(ticket.SessionId, ticket.Secret));
    }

    [Fact]
    public void Daily_minutes_one_session_per_account_and_global_capacity_are_enforced()
    {
        var sessions = new ChatbotVoiceSessions(new() { DailySecondsPerUser = 120, MaxSessionSeconds = 300, GlobalConcurrentSessions = 1 }, TimeProvider.System);
        Assert.Equal(120, sessions.RemainingSeconds(0));
        Assert.Equal(20, sessions.RemainingSeconds(100));
        using (sessions.Enter(Vendor))
        {
            Assert.Equal("voice_active", Assert.Throws<ChatbotException>(() => sessions.Issue(Vendor, "c", null, false, 0)).Code);
            Assert.Equal("voice_busy", Assert.Throws<ChatbotException>(() => sessions.Enter(Vendor with { UserId = 9 })).Code);
        }
        Assert.Equal("voice_daily_limit", Assert.Throws<ChatbotException>(() => sessions.Issue(Vendor, "c", null, false, 115)).Code);
        using var other = sessions.Enter(Vendor with { UserId = 9 });
    }

    [Fact]
    public async Task Spoken_turn_streams_captions_and_audio_handles_barge_in_and_is_saved_as_voice_text()
    {
        using var f = new Fixture();
        f.Upstream.Script(new ChatbotVoiceInputText("Phí tháng này "), new ChatbotVoiceInputText("của tôi?"),
            new ChatbotVoiceAudio([1, 2, 3, 4]), new ChatbotVoiceOutputText("Bạn cần trả"),
            new ChatbotVoiceInterrupted(), new ChatbotVoiceAudio([5, 6]), new ChatbotVoiceUsage(new(300, 40)), new ChatbotVoiceTurnComplete());
        f.Client.EndAfter(() => f.Client.Has("turn_saved"));

        await f.Service.RunAsync(f.Ticket, f.Client, default);

        Assert.Equal(["Phí tháng này ", "của tôi?"], f.Client.Texts("input_transcript"));
        Assert.Equal([1, 2], f.Client.Audio.Select(a => a.Generation).Distinct());
        Assert.Contains(f.Client.Sent, m => m.GetProperty("type").GetString() == "interrupted" && m.GetProperty("generation").GetInt32() == 1);
        f.Store.Verify(s => s.StartAsync(Vendor, "conversation", It.Is<ChatbotSendRequest>(r => r.Channel == "VOICE" && r.Content == "Phí tháng này của tôi?"), It.IsAny<CancellationToken>()));
        Assert.Equal("COMPLETED", f.Saved!.Status);
        Assert.StartsWith("Bạn cần trả", f.Saved.Content);
        Assert.Contains("Đã dừng khi bạn nói tiếp", f.Saved.Content);
        f.Store.Verify(s => s.SaveAsync(Vendor, It.IsAny<ChatbotMessage>(), "FAKE", "fake-live", new ChatbotUsage(300, 40), It.IsAny<CancellationToken>()));
        Assert.Equal("USER", f.Client.Last("turn_saved").GetProperty("userMessage").GetProperty("sender").GetString());
        var ended = f.Client.Last("ended");
        Assert.Equal("ended", ended.GetProperty("reason").GetString());
        var summary = ended.GetProperty("summary");
        Assert.Equal(1, summary.GetProperty("turns").GetInt32());
        Assert.Equal(300, summary.GetProperty("inputTokens").GetInt32());
        Assert.Equal(JsonValueKind.Number, summary.GetProperty("replyMedianMs").ValueKind);
        f.Store.Verify(s => s.RecordVoiceAsync(Vendor, f.Ticket.SessionId, It.IsAny<int>(), "ended", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Provider_go_away_resumes_the_same_conversation_with_the_latest_handle()
    {
        using var f = new Fixture();
        var second = new FakeUpstream();
        f.Provider.Next.Enqueue(second);
        f.Upstream.Script(new ChatbotVoiceResumeHandle("h1"), new ChatbotVoiceResumeHandle("h2"), new ChatbotVoiceGoAway());
        second.Script(new ChatbotVoiceOutputText("Mình vẫn ở đây"), new ChatbotVoiceTurnComplete());
        f.Client.EndAfter(() => f.Client.Has("turn_saved"));

        await f.Service.RunAsync(f.Ticket, f.Client, default);

        Assert.Equal([null, "h2"], f.Provider.Handles);
        Assert.Contains(f.Client.Sent, m => m.GetProperty("type").GetString() == "state" && m.GetProperty("state").GetString() == "reconnecting");
        Assert.Equal("Mình vẫn ở đây", f.Saved!.Content);
        Assert.Equal(1, f.Client.Last("ended").GetProperty("summary").GetProperty("reconnects").GetInt32());
    }

    [Fact]
    public async Task Go_away_without_a_resumption_handle_ends_cleanly()
    {
        using var f = new Fixture();
        f.Upstream.Script(new ChatbotVoiceGoAway());
        await f.Service.RunAsync(f.Ticket, f.Client, default);
        Assert.Equal("provider_reconnect", f.Client.Last("ended").GetProperty("reason").GetString());
        Assert.Single(f.Provider.Handles);
    }

    [Fact]
    public async Task Daily_minutes_come_from_the_database_so_restarts_do_not_reset_them()
    {
        using var f = new Fixture();
        f.Store.Setup(s => s.VoiceSecondsTodayAsync(Vendor.UserId, It.IsAny<CancellationToken>())).ReturnsAsync(1195);
        await f.Service.RunAsync(f.Ticket, f.Client, default);
        Assert.Equal("voice_daily_limit", f.Client.Last("error").GetProperty("code").GetString());
        Assert.False(f.Provider.Connected);
        f.Store.Verify(s => s.RecordVoiceAsync(It.IsAny<ChatbotActor>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Model_tool_calls_run_with_the_server_actor_and_cards_reach_the_screen()
    {
        using var f = new Fixture();
        f.Sender.Setup(s => s.Send(It.IsAny<GetFinanceSummaryQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FinanceSummaryDto(100000, 0, 100000, 0, null));
        f.Upstream.Script(new ChatbotVoiceToolCalls([new("call-1", "vendor.finance", "{}"), new("call-2", "admin.reports", "{}")]));
        f.Client.EndAfter(() => f.Upstream.Replies.Count == 2);

        await f.Service.RunAsync(f.Ticket, f.Client, default);

        Assert.Contains(f.Upstream.Replies, r => r.Id == "call-1" && r.Json.Contains("100.000"));
        // A tool outside the role is answered with a refusal for the model, never executed.
        Assert.Contains(f.Upstream.Replies, r => r.Id == "call-2" && r.Json.Contains("error"));
        var done = Assert.Single(f.Client.Sent, m => m.GetProperty("type").GetString() == "tool" && m.GetProperty("status").GetString() == "done");
        Assert.Equal(JsonValueKind.Array, done.GetProperty("cards").ValueKind);
        Assert.Contains(f.Client.Sent, m => m.GetProperty("type").GetString() == "tool" && m.GetProperty("status").GetString() == "failed");
        f.Store.Verify(s => s.AuditToolAsync(Vendor, f.Ticket.SessionId, "admin.reports", "DENIED_OR_UNAVAILABLE", It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task Malformed_audio_from_the_browser_ends_the_session()
    {
        using var f = new Fixture();
        f.Client.Push(new ChatbotVoiceClientAudio(new byte[3]));
        await f.Service.RunAsync(f.Ticket, f.Client, default);
        Assert.Equal("invalid_audio", f.Client.Last("ended").GetProperty("reason").GetString());
        Assert.Empty(f.Upstream.AudioSent);
    }

    [Fact]
    public async Task Muted_microphone_is_not_forwarded()
    {
        using var f = new Fixture();
        f.Client.Push(new ChatbotVoiceClientControl("mute", true), new ChatbotVoiceClientAudio(new byte[320]),
            new ChatbotVoiceClientControl("mute", false), new ChatbotVoiceClientAudio(new byte[640]), new ChatbotVoiceClientControl("end"));
        await f.Service.RunAsync(f.Ticket, f.Client, default);
        Assert.Equal([640], f.Upstream.AudioSent);
        Assert.Equal(1, f.Upstream.AudioEnds);
    }

    [Fact]
    public async Task Revoked_login_never_connects_to_the_provider()
    {
        using var f = new Fixture(); f.Active = false;
        await f.Service.RunAsync(f.Ticket, f.Client, default);
        Assert.Equal("session_expired", f.Client.Last("error").GetProperty("code").GetString());
        Assert.False(f.Provider.Connected);
        using var again = f.Sessions.Enter(Vendor);
    }

    [Fact]
    public async Task Unavailable_provider_reports_a_text_fallback_and_releases_the_slot()
    {
        using var f = new Fixture(); f.Provider.Fail = true;
        await f.Service.RunAsync(f.Ticket, f.Client, default);
        Assert.Equal("voice_unavailable", f.Client.Last("error").GetProperty("code").GetString());
        using var again = f.Sessions.Enter(Vendor);
    }

    [Fact]
    public void Voice_instruction_keeps_role_rules_and_adds_easy_mode_only_when_asked()
    {
        var plain = ChatbotVoiceService.Instruction(Vendor, false);
        Assert.Contains("Không tự duyệt", plain);
        Assert.Contains("không thể thay đổi vai trò", plain);
        Assert.DoesNotContain("chế độ dễ dùng", plain);
        Assert.Contains("chế độ dễ dùng", ChatbotVoiceService.Instruction(Vendor, true));
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeProvider(FakeUpstream upstream) : IChatbotVoiceProvider
    {
        public bool Fail; public bool Connected;
        public readonly Queue<FakeUpstream> Next = new();
        public readonly List<string?> Handles = [];
        public string Name => "FAKE"; public bool Available => true;
        public Task<IChatbotVoiceUpstream> ConnectAsync(ChatbotVoiceSetup setup, CancellationToken ct, string? resumeHandle = null)
        {
            if (Fail) throw new ChatbotProviderException("provider_unavailable", true);
            Handles.Add(resumeHandle);
            var target = Connected && Next.Count > 0 ? Next.Dequeue() : upstream;
            Connected = true;
            return Task.FromResult<IChatbotVoiceUpstream>(target);
        }
    }

    private sealed class FakeUpstream : IChatbotVoiceUpstream
    {
        private readonly Channel<ChatbotVoiceEvent> events = Channel.CreateUnbounded<ChatbotVoiceEvent>();
        public readonly List<int> AudioSent = []; public int AudioEnds; public readonly List<ChatbotToolReply> Replies = [];
        public string Model => "fake-live";
        public void Script(params ChatbotVoiceEvent[] items) { foreach (var item in items) events.Writer.TryWrite(item); }
        public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16k, CancellationToken ct) { lock (AudioSent) AudioSent.Add(pcm16k.Length); return Task.CompletedTask; }
        public Task SendAudioEndAsync(CancellationToken ct) { AudioEnds++; return Task.CompletedTask; }
        public Task SendToolResponsesAsync(IReadOnlyList<ChatbotToolReply> replies, CancellationToken ct) { lock (Replies) Replies.AddRange(replies); return Task.CompletedTask; }
        public async IAsyncEnumerable<ChatbotVoiceEvent> EventsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            while (await events.Reader.WaitToReadAsync(ct))
                while (events.Reader.TryRead(out var item)) yield return item;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeClient : IChatbotVoiceClient
    {
        private readonly Channel<ChatbotVoiceClientFrame?> frames = Channel.CreateUnbounded<ChatbotVoiceClientFrame?>();
        private Func<bool>? endWhen;
        public readonly List<JsonElement> Sent = [];
        public readonly List<(int Generation, int Length)> Audio = [];
        public void Push(params ChatbotVoiceClientFrame[] items) { foreach (var item in items) frames.Writer.TryWrite(item); }
        public void EndAfter(Func<bool> condition) => endWhen = condition;
        public bool Has(string type) { lock (Sent) return Sent.Any(m => m.GetProperty("type").GetString() == type); }
        public string[] Texts(string type) { lock (Sent) return Sent.Where(m => m.GetProperty("type").GetString() == type).Select(m => m.GetProperty("text").GetString()!).ToArray(); }
        public JsonElement Last(string type) { lock (Sent) return Sent.Last(m => m.GetProperty("type").GetString() == type); }

        public async Task<ChatbotVoiceClientFrame?> ReceiveAsync(CancellationToken ct)
        {
            using var poll = CancellationTokenSource.CreateLinkedTokenSource(ct);
            while (true)
            {
                if (frames.Reader.TryRead(out var frame)) return frame;
                if (endWhen?.Invoke() == true) return new ChatbotVoiceClientControl("end");
                await Task.Delay(10, ct);
            }
        }
        public Task SendAsync(object message, CancellationToken ct)
        {
            lock (Sent) Sent.Add(JsonSerializer.SerializeToElement(message, ChatbotJson.Options));
            return Task.CompletedTask;
        }
        public Task SendAudioAsync(int generation, ReadOnlyMemory<byte> pcm, CancellationToken ct) { lock (Audio) Audio.Add((generation, pcm.Length)); return Task.CompletedTask; }
    }

    private sealed class Fixture : IDisposable
    {
        public bool Active = true;
        public readonly Mock<IChatbotActorResolver> Actors = new();
        public readonly Mock<IChatbotStore> Store = new();
        public readonly Mock<ISender> Sender = new();
        public readonly FakeUpstream Upstream = new();
        public readonly FakeProvider Provider;
        public readonly FakeClient Client = new();
        public readonly ChatbotVoiceSessions Sessions;
        public readonly ChatbotVoiceTicket Ticket;
        public ChatbotMessage? Saved;
        public readonly ChatbotVoiceService Service;

        public Fixture()
        {
            Provider = new(Upstream);
            var voice = new ChatbotVoiceSettings { Enabled = true, Model = "fake-live" };
            Sessions = new(voice, TimeProvider.System);
            Ticket = Sessions.Issue(Vendor, "conversation", null, false, 0);
            Actors.Setup(a => a.IsActiveAsync(It.IsAny<ChatbotActor>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => Active);
            var now = DateTimeOffset.UtcNow;
            var user = new ChatbotMessage("u", "conversation", "r", "USER", "COMPLETED", "", false, [], [], [], now, now, 1, 1, Channel: "VOICE");
            var answer = user with { Id = "a", Sender = "ASSISTANT", Status = "GENERATING", IsAiGenerated = true, Ordinal = 2 };
            Store.Setup(s => s.StartAsync(It.IsAny<ChatbotActor>(), It.IsAny<string>(), It.IsAny<ChatbotSendRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ChatbotStart(new(user, answer), false));
            Store.Setup(s => s.SaveAsync(It.IsAny<ChatbotActor>(), It.IsAny<ChatbotMessage>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<ChatbotUsage>(), It.IsAny<CancellationToken>()))
                .Callback((ChatbotActor _, ChatbotMessage m, string? _, string? _, ChatbotUsage _, CancellationToken _) => Saved = m).Returns(Task.CompletedTask);
            Store.Setup(s => s.MessageAsync(It.IsAny<ChatbotActor>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => Saved!);
            var tools = new ChatbotTools(Sender.Object, Actors.Object, new(), TimeProvider.System, Mock.Of<IChatbotListReader>());
            Service = new(Actors.Object, Store.Object, tools, [Provider], Sessions, voice, new(), TimeProvider.System);
        }

        public void Dispose() { }
    }
}
