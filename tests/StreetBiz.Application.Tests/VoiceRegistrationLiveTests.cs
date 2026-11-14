using System.Text.Json;
using System.Threading.Channels;
using FluentAssertions;
using MediatR;
using Moq;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.AdministrativeUnits;
using StreetBiz.Application.DTOs.Sidewalk;
using StreetBiz.Application.Features.AdministrativeUnits.ListWards;
using StreetBiz.Application.Features.AiAssistance;
using StreetBiz.Application.Features.Chatbot;
using StreetBiz.Application.Features.SidewalkSlots.SearchSlots;
using StreetBiz.Application.Features.VendorRegistration.AssistTurn;

namespace StreetBiz.Application.Tests;

/// <summary>X01 v2: the live conversation keeps every v1 rule -- the model only proposes, the server decides.</summary>
public sealed class VoiceRegistrationLiveTests
{
    private static readonly ChatbotActor Vendor = new(7, 3, "VENDOR", null, 70);

    private static readonly IReadOnlyList<WardDto> Wards =
    [
        new(10, "Phường Hải Châu", "Thành phố Đà Nẵng"),
        new(11, "Phường Thanh Khê", "Thành phố Đà Nẵng"),
    ];

    /// <summary>Everything asked before the amounts: the next question is about money.</summary>
    private static readonly AssistDraft BeforeMoney = new(
        BusinessLine: "Bún bò", VendorType: VendorTypes.Itinerant, LocationSkipped: true, WardUnitId: 10, Salutation: "cô");

    [Fact]
    public async Task The_model_opens_with_the_planners_question_and_knows_only_the_salutation()
    {
        using var f = new Fixture(new AssistDraft(Salutation: "cô"));
        f.Client.EndAfter(() => f.Upstream.Texts.Count > 0);

        await f.Run();

        f.Provider.Setup!.BlockingTools.Should().BeTrue();
        f.Provider.Setup.Tools.Should().ContainSingle(t => t.Name == RegistrationVoiceService.ToolName);
        f.Provider.Setup.SystemPrompt.Should().Contain("Gọi người bán là \"cô\", tự xưng là \"cháu\"")
            .And.Contain("Lời nói \"đúng rồi\" không thay được nút")
            .And.Contain("Không được: hứa hồ sơ sẽ được duyệt");
        f.Upstream.Texts[0].Should().StartWith("[Màn hình] Bắt đầu phiên trò chuyện.")
            .And.Contain("Câu hỏi tiếp theo (next_input: VOICE): \"Cô bán món gì, thường bán ở đâu, mấy giờ ạ?\"");
        var opening = f.Client.First("assist");
        opening.GetProperty("version").GetInt32().Should().Be(0);
        opening.GetProperty("result").GetProperty("nextQuestion").GetProperty("key").GetString().Should().Be(AssistQuestionKeys.Opening);
    }

    [Fact]
    public void The_draft_the_model_can_be_told_about_has_nothing_read_from_the_CCCD()
    {
        // Structural guarantee behind "chỉ gửi xưng hô": the draft has no place for a name, number,
        // birth date or address from the card, so neither the prompt nor a screen message can carry one.
        typeof(AssistDraft).GetProperties().Select(p => p.Name).Should().BeEquivalentTo(
            "BusinessLine", "VendorType", "LocationText", "Latitude", "Longitude", "LocationConfirmed", "LocationSkipped",
            "WardUnitId", "DeclaredAddress", "CapitalAmount", "LaborCount", "PlannedStartDate", "ContactSameAsPermanent",
            "Hours", "CartWidthMeters", "CartWidthSkipped", "Confirmed", "Attempts", "Salutation");
    }

    [Fact]
    public async Task A_spoken_answer_passes_the_v1_guards_before_it_reaches_the_screen_or_the_model()
    {
        using var f = new Fixture(new AssistDraft(Salutation: "cô"));
        f.Geocoding.Setup(g => g.SearchAsync("Chợ Cồn, Đà Nẵng", 3, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new GeoPoint(16.068m, 108.214m, "Chợ Cồn, Hải Châu, Đà Nẵng")]);
        f.Upstream.Script(
            new ChatbotVoiceInputText("Tôi bán bún bò với bánh mì ngoài chợ Cồn á. "),
            new ChatbotVoiceInputText("Sáng 6:00 tới 10:00."),
            Call("c1",
                ("businessLine", "Bún bò và bánh mì", "bún bò với bánh mì"),
                ("locationText", "Chợ Cồn", "ngoài chợ Cồn á"),
                ("hours", "06:00-10:00", "Sáng 6:00 tới 10:00"),
                // Never said: the model made it up.
                ("capitalAmount", "50000000", "vốn năm chục triệu")));
        f.Client.EndAfter(() => f.Upstream.Replies.Count > 0);

        await f.Run();

        var assist = f.Client.Last("assist");
        assist.GetProperty("cause").GetString().Should().Be("voice");
        assist.GetProperty("version").GetInt32().Should().Be(1);
        var result = assist.GetProperty("result");
        result.GetProperty("proposals").EnumerateArray().Select(p => p.GetProperty("field").GetString())
            .Should().BeEquivalentTo("businessLine", "locationText", "hours");
        result.GetProperty("draft").GetProperty("capitalAmount").ValueKind.Should().Be(JsonValueKind.Null);
        result.GetProperty("location").GetProperty("latitude").GetDecimal().Should().Be(16.068m);
        result.GetProperty("isAiGenerated").GetBoolean().Should().BeTrue();

        using var reply = JsonDocument.Parse(f.Upstream.Replies.Single().Json);
        reply.RootElement.GetProperty("next_question").GetString().Should().Be("Cô bán bằng xe đẩy, gánh, hay có cửa hàng cố định?");
        reply.RootElement.GetProperty("next_input").GetString().Should().Be(AssistInputs.Choice);
        reply.RootElement.GetProperty("notAccepted").EnumerateArray().Select(e => e.GetString()).Should().Equal("vốn");

        f.Logs.Verify(l => l.RecordAsync(AiFeatureCodes.VoiceRegistration, AiLogEntities.UserAccount, Vendor.UserId,
            AssistQuestionKeys.Opening, It.IsAny<object>(), It.IsAny<decimal?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Words_that_arrive_just_after_the_call_still_count_as_evidence()
    {
        using var f = new Fixture(BeforeMoney);
        f.Upstream.Script(Call("c1", ("capitalAmount", "30000000", "ba chục triệu")));
        _ = Task.Delay(150).ContinueWith(_ => f.Upstream.Script(new ChatbotVoiceInputText("Vốn chừng ba chục triệu")));
        f.Client.EndAfter(() => f.Upstream.Replies.Count > 0);

        await f.Run();

        var draft = f.Client.Last("assist").GetProperty("result").GetProperty("draft");
        draft.GetProperty("capitalAmount").GetDecimal().Should().Be(30_000_000);
        Confirmed(draft).Should().BeEmpty();
        using var reply = JsonDocument.Parse(f.Upstream.Replies.Single().Json);
        reply.RootElement.GetProperty("next_question").GetString().Should().Be("Ba mươi triệu đồng, đúng không ạ?");
        reply.RootElement.GetProperty("next_input").GetString().Should().Be(AssistInputs.Confirm);
    }

    [Fact]
    public async Task Saying_yes_never_confirms_an_amount_only_the_tap_does()
    {
        using var f = new Fixture(BeforeMoney with { CapitalAmount = 30_000_000 });
        f.Upstream.Script(
            new ChatbotVoiceInputText("Đúng rồi con"),
            Call("c1", ("capitalAmount", "30000000", "Đúng rồi")));
        f.Client.EndAfter(() => f.Upstream.Replies.Count > 0);

        await f.Run();

        var result = f.Client.Last("assist").GetProperty("result");
        Confirmed(result.GetProperty("draft")).Should().BeEmpty();
        result.GetProperty("nextQuestion").GetProperty("key").GetString().Should().Be("CONFIRM:capitalAmount");
    }

    [Fact]
    public async Task A_tap_moves_the_interview_on_and_the_model_is_told_what_was_tapped()
    {
        var draft = BeforeMoney with { CapitalAmount = 30_000_000 };
        using var f = new Fixture(draft);
        f.Client.Push(Tap(0, draft with { Confirmed = [AssistFields.CapitalAmount] }));
        f.Client.EndAfter(() => f.Upstream.Texts.Count > 1);

        await f.Run();

        var assist = f.Client.Last("assist");
        assist.GetProperty("cause").GetString().Should().Be("screen");
        Confirmed(assist.GetProperty("result").GetProperty("draft")).Should().Equal(AssistFields.CapitalAmount);
        f.Upstream.Texts[1].Should().Be(
            "[Màn hình] Người bán vừa bấm \"Đúng rồi\" cho vốn. Câu hỏi tiếp theo (next_input: VOICE): \"Có mấy người cùng bán, tính cả cô ạ?\" Không gọi propose_fields cho tin này.");
    }

    [Fact]
    public async Task A_call_with_no_new_words_from_the_vendor_is_not_a_misheard_try()
    {
        using var f = new Fixture(BeforeMoney);
        // The model reacts to a screen message by calling the tool, though the vendor said nothing.
        f.Upstream.Script(Call("c1", ("capitalAmount", "30000000", "ba chục triệu")));
        f.Client.EndAfter(() => f.Upstream.Replies.Count > 0);

        await f.Run();

        f.Client.Count("assist").Should().Be(1); // only the opening screen
        using var reply = JsonDocument.Parse(f.Upstream.Replies.Single().Json);
        reply.RootElement.GetProperty("next_question").GetString().Should().Be("Vốn bỏ ra ban đầu khoảng bao nhiêu ạ?");
        f.Logs.Verify(l => l.RecordAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string?>(),
            It.IsAny<object>(), It.IsAny<decimal?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_tap_on_an_out_of_date_screen_is_refused_and_the_screen_refreshed()
    {
        using var f = new Fixture(BeforeMoney with { CapitalAmount = 30_000_000 });
        f.Client.Push(Tap(4, BeforeMoney with { CapitalAmount = 30_000_000, Confirmed = [AssistFields.CapitalAmount] }));
        f.Client.EndAfter(() => f.Client.Count("assist") > 1);

        await f.Run();

        f.Client.Last("warning").GetProperty("code").GetString().Should().Be("stale_draft");
        Confirmed(f.Client.Last("assist").GetProperty("result").GetProperty("draft")).Should().BeEmpty();
        f.Upstream.Texts.Should().ContainSingle();
    }

    [Fact]
    public async Task Typed_text_is_the_vendors_words_for_the_evidence_check()
    {
        using var f = new Fixture(BeforeMoney);
        f.Client.Push(new ChatbotVoiceClientControl("text", Json: """{"type":"text","text":"vốn 15 triệu"}"""));
        f.Upstream.OnText = text =>
        {
            if (text.Contains("«vốn 15 triệu»", StringComparison.Ordinal))
                f.Upstream.Script(Call("c1", ("capitalAmount", "15000000", "vốn 15 triệu")));
        };
        f.Client.EndAfter(() => f.Upstream.Replies.Count > 0);

        await f.Run();

        f.Client.Last("assist").GetProperty("result").GetProperty("draft").GetProperty("capitalAmount").GetDecimal().Should().Be(15_000_000);
        f.Logs.Verify(l => l.RecordAsync(AiFeatureCodes.VoiceRegistration, AiLogEntities.UserAccount, Vendor.UserId,
            AssistQuestionKeys.Capital, It.Is<object>(o => JsonSerializer.Serialize(o, (JsonSerializerOptions?)null).Contains("\"input\":\"TEXT\"")),
            It.IsAny<decimal?>(), It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task Unavailable_live_voice_tells_the_client_to_fall_back_and_frees_the_slot()
    {
        using var f = new Fixture(new AssistDraft());
        f.Provider.Fail = true;

        await f.Run();

        f.Client.Last("error").GetProperty("code").GetString().Should().Be("voice_unavailable");
        f.Client.Last("ended").GetProperty("reason").GetString().Should().Be("voice_unavailable");
        using var again = f.Sessions.Enter(Vendor);
    }

    [Fact]
    public async Task A_silent_session_ends_by_itself_and_says_why_before_the_line_is_dropped()
    {
        using var f = new Fixture(new AssistDraft(), new RegistrationVoiceSettings { IdleSeconds = 2 });

        await f.Run();

        f.Client.Last("ended").GetProperty("reason").GetString().Should().Be("idle");
        // Cancelling a pending read aborts a real WebSocket: it must not happen before "ended" went out.
        f.Client.ReadCancelledBeforeEnded.Should().BeFalse();
    }

    [Fact]
    public async Task A_backlog_of_microphone_audio_is_dropped_not_the_interview()
    {
        using var f = new Fixture(new AssistDraft());
        // 10 s of audio handed over at once, as a phone does after its app stalled.
        f.Client.Push(Enumerable.Range(0, 10).Select(_ => (ChatbotVoiceClientFrame)new ChatbotVoiceClientAudio(new byte[32_000])).ToArray());
        f.Client.EndAfter(() => f.Upstream.AudioBytes > 0);

        await f.Run();

        f.Client.Last("ended").GetProperty("reason").GetString().Should().Be("ended");
        f.Upstream.AudioBytes.Should().BeLessOrEqualTo(96_000);
    }

    [Fact]
    public async Task The_session_counts_against_the_daily_voice_minutes()
    {
        using var f = new Fixture(new AssistDraft());
        f.Client.EndAfter(() => f.Upstream.Texts.Count > 0);

        await f.Run();

        f.Store.Verify(s => s.RecordVoiceAsync(Vendor, f.Ticket.SessionId, It.IsAny<int>(), "x01:ended", It.IsAny<CancellationToken>()));
    }

    [Fact]
    public void A_tap_confirms_only_what_was_read_back_or_typed_in_the_box()
    {
        var server = BeforeMoney with { CapitalAmount = 30_000_000, Attempts = new Dictionary<string, int> { [AssistQuestionKeys.Labor] = 1 } };
        var readBack = new AssistQuestion("CONFIRM:capitalAmount", "Ba mươi triệu đồng, đúng không ạ?", AssistInputs.Confirm, [], AssistFields.CapitalAmount);

        RegistrationVoiceService.Sanitize(server, server with { Confirmed = [AssistFields.CapitalAmount] }, readBack)
            .Confirmed.Should().Equal(AssistFields.CapitalAmount);
        RegistrationVoiceService.Sanitize(server, server with { CapitalAmount = 50_000_000, Confirmed = [AssistFields.CapitalAmount] }, readBack)
            .Confirmed.Should().BeEmpty();

        var typing = new AssistQuestion(AssistQuestionKeys.Labor, "Gõ giúp", AssistInputs.Manual, [], AssistFields.LaborCount);
        var typed = RegistrationVoiceService.Sanitize(server, server with { LaborCount = 3, Confirmed = [AssistFields.LaborCount], Salutation = "chú" }, typing);
        typed.Confirmed.Should().Equal(AssistFields.LaborCount);
        typed.Salutation.Should().Be("cô");
        typed.Attempts![AssistQuestionKeys.Labor].Should().Be(1);
    }

    [Fact]
    public void Only_the_guessed_wards_are_read_out_never_the_whole_list()
    {
        var all = Enumerable.Range(0, 94).Select(i => new AssistChoice($"{i}", $"Phường {i}")).ToList();
        RegistrationVoiceService.SpokenChoices(new(AssistQuestionKeys.Ward, "Phường, xã nào?", AssistInputs.Choice, all, SuggestedCount: 2))
            .Should().Equal("Phường 0", "Phường 1");
        RegistrationVoiceService.SpokenChoices(new(AssistQuestionKeys.Ward, "Phường, xã nào?", AssistInputs.Choice, all)).Should().BeEmpty();
        RegistrationVoiceService.SpokenChoices(new(AssistQuestionKeys.VendorType, "Bán thế nào?", AssistInputs.Choice,
            [new("ITINERANT", "Xe đẩy"), new("FIXED_STOREFRONT", "Cửa hàng")])).Should().Equal("Xe đẩy", "Cửa hàng");
    }

    [Fact]
    public void A_read_back_is_a_correction_of_its_own_field()
    {
        RegistrationVoiceService.AnswerKey(new("CONFIRM:laborCount", "Hai người, đúng không ạ?", AssistInputs.Confirm, [], AssistFields.LaborCount))
            .Should().Be(AssistQuestionKeys.Labor);
        RegistrationVoiceService.AnswerKey(new(AssistQuestionKeys.Ward, "Phường nào?", AssistInputs.Choice, []))
            .Should().Be(AssistQuestionKeys.Ward);
        RegistrationVoiceService.AnswerKey(null).Should().Be("DONE");
    }

    [Fact]
    public void Only_the_closed_list_of_fields_with_evidence_is_read_from_the_call()
    {
        var fields = RegistrationVoiceService.ParseFields("""
            {"fields":[
              {"field":"món bán","value":"Bún bò","evidence":"bún bò"},
              {"field":"laborCount","value":2,"evidence":"hai người","confidence":3},
              {"field":"hours","value":"06:00-10:00"},
              {"field":"contactSameAsPermanent","value":true,"evidence":"đúng rồi"}
            ]}
            """);

        fields.Should().BeEquivalentTo(new[]
        {
            new ExtractedField("laborCount", "2", 1m, "hai người"),
            new ExtractedField("contactSameAsPermanent", "true", 0.8m, "đúng rồi"),
        });
        RegistrationVoiceService.ParseFields("not json").Should().BeEmpty();
    }

    [Fact]
    public void Tickets_are_single_use_and_need_the_secret()
    {
        var tickets = new RegistrationVoiceTickets(new ChatbotVoiceSettings(), TimeProvider.System);
        var ticket = tickets.Issue(Vendor, new AssistDraft(), 600);
        tickets.Redeem(ticket.SessionId, "guess").Should().BeNull();
        tickets.Redeem(ticket.SessionId, ticket.Secret).Should().BeNull();

        var second = tickets.Issue(Vendor, new AssistDraft(), 600);
        tickets.Redeem(second.SessionId, second.Secret).Should().Be(second);
        tickets.Redeem(second.SessionId, second.Secret).Should().BeNull();
    }

    [Fact]
    public async Task A_ticket_needs_voice_consent_a_vendor_and_live_voice_switched_on()
    {
        new StartAssistLiveCommandValidator().Validate(new StartAssistLiveCommand(new AssistDraft(), false))
            .Errors.Should().ContainSingle(e => e.ErrorMessage == "Cần đồng ý cho phép ghi âm trước khi nói.");

        var actors = new Mock<IChatbotActorResolver>();
        var store = new Mock<IChatbotStore>();
        var voice = new ChatbotVoiceSettings { Enabled = true, Model = "fake-live" };
        var provider = new FakeProvider(new FakeUpstream());
        StartAssistLiveCommandHandler Handler(RegistrationVoiceSettings live) => new(
            actors.Object, store.Object, [provider], voice, new ChatbotVoiceSessions(voice, TimeProvider.System), live,
            new RegistrationVoiceTickets(voice, TimeProvider.System));
        var command = new StartAssistLiveCommand(new AssistDraft(Salutation: "chị"), true);

        actors.Setup(a => a.RequireAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Vendor with { Role = "CUSTOMER" });
        await FluentActions.Awaiting(() => Handler(new()).Handle(command, default)).Should().ThrowAsync<ForbiddenException>();

        actors.Setup(a => a.RequireAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Vendor);
        (await FluentActions.Awaiting(() => Handler(new() { Enabled = false }).Handle(command, default))
            .Should().ThrowAsync<VoiceSessionException>()).Which.ErrorCode.Should().Be("voice_unavailable");

        store.Setup(s => s.VoiceSecondsTodayAsync(Vendor.UserId, It.IsAny<CancellationToken>())).ReturnsAsync(voice.DailySecondsPerUser - 20);
        (await FluentActions.Awaiting(() => Handler(new()).Handle(command, default))
            .Should().ThrowAsync<VoiceSessionException>()).Which.StatusCode.Should().Be(429);

        store.Setup(s => s.VoiceSecondsTodayAsync(Vendor.UserId, It.IsAny<CancellationToken>())).ReturnsAsync(0);
        var ticket = await Handler(new()).Handle(command, default);
        ticket.MaxSeconds.Should().Be(600);
        ticket.StreamPath.Should().Be($"/hubs/registration-voice/{ticket.SessionId}");
        ticket.InputSampleRate.Should().Be(16000);
        ticket.OutputSampleRate.Should().Be(24000);
    }

    private static string?[] Confirmed(JsonElement draft) =>
        draft.GetProperty("confirmed") is { ValueKind: JsonValueKind.Array } list ? list.EnumerateArray().Select(e => e.GetString()).ToArray() : [];

    private static ChatbotVoiceToolCalls Call(string id, params (string Field, string Value, string Evidence)[] fields) =>
        new([new ChatbotToolCall(id, RegistrationVoiceService.ToolName, JsonSerializer.Serialize(new
        {
            fields = fields.Select(f => new { field = f.Field, value = f.Value, evidence = f.Evidence }),
        }))]);

    private static ChatbotVoiceClientControl Tap(int version, AssistDraft draft) =>
        new("draft", Json: JsonSerializer.Serialize(new { type = "draft", version, draft }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    private static SidewalkSlotDto Slot(long id, string code, int ward, double distance) => new(
        id, code, 1, "Nguyễn Văn Linh", ward, 16.07m, 108.21m, 1.5m, 2m, "AVAILABLE", "WARD", 50_000,
        new TimeOnly(5, 0), new TimeOnly(11, 0), distance, [], false, false, false, null, null, null,
        "DAY", null, "STANDARD", null, 1_500_000, null);

    private sealed class FakeProvider(FakeUpstream upstream) : IChatbotVoiceProvider
    {
        public bool Fail;
        public ChatbotVoiceSetup? Setup;
        public string Name => "FAKE";
        public bool Available => true;

        public Task<IChatbotVoiceUpstream> ConnectAsync(ChatbotVoiceSetup setup, CancellationToken ct, string? resumeHandle = null)
        {
            if (Fail) throw new ChatbotProviderException("provider_unavailable", true);
            Setup = setup;
            return Task.FromResult<IChatbotVoiceUpstream>(upstream);
        }
    }

    private sealed class FakeUpstream : IChatbotVoiceUpstream
    {
        private readonly Channel<ChatbotVoiceEvent> events = Channel.CreateUnbounded<ChatbotVoiceEvent>();
        public readonly List<ChatbotToolReply> Replies = [];
        public readonly List<string> Texts = [];
        public Action<string>? OnText;
        public string Model => "fake-live";

        public void Script(params ChatbotVoiceEvent[] items)
        {
            foreach (var item in items) events.Writer.TryWrite(item);
        }

        public int AudioBytes;

        public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16k, CancellationToken ct)
        {
            Interlocked.Add(ref AudioBytes, pcm16k.Length);
            return Task.CompletedTask;
        }

        public Task SendAudioEndAsync(CancellationToken ct) => Task.CompletedTask;

        public Task SendToolResponsesAsync(IReadOnlyList<ChatbotToolReply> replies, CancellationToken ct)
        {
            lock (Replies) Replies.AddRange(replies);
            return Task.CompletedTask;
        }

        public Task SendTextAsync(string text, CancellationToken ct)
        {
            lock (Texts) Texts.Add(text);
            OnText?.Invoke(text);
            return Task.CompletedTask;
        }

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
        private readonly List<JsonElement> sent = [];

        public void Push(params ChatbotVoiceClientFrame[] items)
        {
            foreach (var item in items) frames.Writer.TryWrite(item);
        }

        public void EndAfter(Func<bool> condition) => endWhen = condition;
        public bool ReadCancelledBeforeEnded;
        public int Count(string type) { lock (sent) return sent.Count(m => m.GetProperty("type").GetString() == type); }
        public JsonElement First(string type) { lock (sent) return sent.First(m => m.GetProperty("type").GetString() == type); }
        public JsonElement Last(string type) { lock (sent) return sent.Last(m => m.GetProperty("type").GetString() == type); }

        public async Task<ChatbotVoiceClientFrame?> ReceiveAsync(CancellationToken ct)
        {
            using var watch = ct.Register(() =>
            {
                lock (sent) ReadCancelledBeforeEnded |= !sent.Any(m => m.GetProperty("type").GetString() == "ended");
            });
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(10));
            while (true)
            {
                if (frames.Reader.TryRead(out var frame)) return frame;
                if (endWhen?.Invoke() == true) return new ChatbotVoiceClientControl("end");
                await Task.Delay(10, limit.Token);
            }
        }

        public Task SendAsync(object message, CancellationToken ct)
        {
            lock (sent) sent.Add(JsonSerializer.SerializeToElement(message, ChatbotJson.Options));
            return Task.CompletedTask;
        }

        public Task SendAudioAsync(int generation, ReadOnlyMemory<byte> pcm, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Fixture : IDisposable
    {
        public readonly Mock<IChatbotActorResolver> Actors = new();
        public readonly Mock<IChatbotStore> Store = new();
        public readonly Mock<IGeocodingService> Geocoding = new();
        public readonly Mock<ISender> Sender = new();
        public readonly Mock<IAiAssistanceLogs> Logs = new();
        public readonly FakeUpstream Upstream = new();
        public readonly FakeProvider Provider;
        public readonly FakeClient Client = new();
        public readonly ChatbotVoiceSessions Sessions;
        public readonly RegistrationVoiceTicket Ticket;
        private readonly RegistrationVoiceService service;

        public Fixture(AssistDraft draft, RegistrationVoiceSettings? settings = null)
        {
            Provider = new(Upstream);
            var voice = new ChatbotVoiceSettings { Enabled = true, Model = "fake-live" };
            Sessions = new(voice, TimeProvider.System);
            Ticket = new RegistrationVoiceTickets(voice, TimeProvider.System).Issue(Vendor, draft, 600);
            Actors.Setup(a => a.IsActiveAsync(It.IsAny<ChatbotActor>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
            Sender.Setup(s => s.Send(It.IsAny<ListWardsQuery>(), It.IsAny<CancellationToken>())).ReturnsAsync(Wards);
            Sender.Setup(s => s.Send(It.IsAny<SearchSlotsQuery>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([Slot(21, "LD-07", 10, 120), Slot(22, "LD-08", 10, 180)]);
            Geocoding.Setup(g => g.SearchAsync(It.IsAny<string>(), 3, It.IsAny<CancellationToken>())).ReturnsAsync([]);
            service = new(Actors.Object, Store.Object, [Provider], Sessions, voice, settings ?? new RegistrationVoiceSettings(),
                Geocoding.Object, Sender.Object, Logs.Object, TimeProvider.System);
        }

        public Task Run() => service.RunAsync(Ticket, Client, default);

        public void Dispose()
        {
        }
    }
}
