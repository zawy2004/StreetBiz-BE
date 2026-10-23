using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Features.VendorRegistration.AssistTurn;
using StreetBiz.Infrastructure.Services;
using StreetBiz.Infrastructure.Services.Voice;

namespace StreetBiz.Infrastructure.Tests;

/// <summary>X01: the speech-to-text and extraction adapters, against stubbed provider responses.</summary>
public sealed class VoiceRegistrationProvidersTests
{
    private sealed class Recorder(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request, body));
            return respond(request, body);
        }
    }

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiCompliance:Groq:ApiKeys:0"] = "gsk_test_key_one",
                ["AiCompliance:Groq:ApiKeys:1"] = "gsk_test_key_two",
                ["AiCompliance:Gemini:ApiKeys:0"] = "gem_test_key_one",
            }.Concat(values.Select(v => KeyValuePair.Create(v.Key, (string?)v.Value))))
            .Build();

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    private static readonly RegistrationExtractionInput Input = new(
        AssistQuestionKeys.Capital, "Vốn bỏ ra ban đầu khoảng bao nhiêu ạ?",
        new AssistDraft(BusinessLine: "Bún bò", VendorType: "ITINERANT", Salutation: "cô"),
        new DateOnly(2026, 10, 22), "Vốn bỏ ra chừng ba chục triệu");

    private const string ExtractionJson = """
        {"businessLine":{"value":null,"confidence":0,"evidence":""},
         "vendorType":{"value":null,"confidence":0,"evidence":""},
         "locationText":{"value":null,"confidence":0,"evidence":""},
         "declaredAddress":{"value":null,"confidence":0,"evidence":""},
         "capitalAmount":{"value":30000000,"confidence":0.95,"evidence":"ba chục triệu"},
         "laborCount":{"value":null,"confidence":0,"evidence":""},
         "plannedStartDate":{"value":null,"confidence":0,"evidence":""},
         "hours":{"value":null,"confidence":0,"evidence":""},
         "cartWidthMeters":{"value":null,"confidence":0,"evidence":""},
         "contactSameAsPermanent":{"value":false,"confidence":0.4,"evidence":"x"}}
        """;

    [Fact]
    public async Task Speech_to_text_forces_Vietnamese_sends_the_vocabulary_and_names_the_file_by_its_format()
    {
        var handler = new Recorder((_, _) => Json(HttpStatusCode.OK, """{"text":" Vốn bỏ ra chừng 30 triệu "}"""));
        var stt = new GroqWhisperSpeechToText(new HttpClient(handler), Config(), new AiKeyPools(Config()), NullLogger<GroqWhisperSpeechToText>.Instance);

        var result = await stt.TranscribeAsync([1, 2, 3], "audio/m4a", CancellationToken.None);

        result.Text.Should().Be("Vốn bỏ ra chừng 30 triệu");
        result.Provider.Should().Be("groq:" + GroqWhisperSpeechToText.DefaultModel);
        var body = handler.Calls.Single().Body;
        body.Should().Contain("name=language").And.Contain("vi").And.Contain("chợ Cồn").And.Contain("filename=clip.m4a");
        handler.Calls.Single().Request.RequestUri!.ToString().Should().Be("https://api.groq.com/openai/v1/audio/transcriptions");
    }

    [Fact]
    public async Task Silence_and_Whispers_known_inventions_are_not_a_transcript()
    {
        // The emulator's silent microphone, 23/10/2026: Whisper "heard" a YouTube outro.
        var handler = new Recorder((_, _) => Json(HttpStatusCode.OK, """
            {"text":"Hãy subscribe cho kênh Ghiền Mì Gõ Để không bỏ lỡ những video hấp dẫn",
             "segments":[{"text":"Hãy subscribe cho kênh Ghiền Mì Gõ","no_speech_prob":0.2},
                         {"text":" Để không bỏ lỡ những video hấp dẫn","no_speech_prob":0.1}]}
            """));
        var stt = new GroqWhisperSpeechToText(new HttpClient(handler), Config(), new AiKeyPools(Config()), NullLogger<GroqWhisperSpeechToText>.Instance);

        (await stt.TranscribeAsync([1], "audio/m4a", CancellationToken.None)).Text.Should().BeEmpty();
        handler.Calls.Single().Body.Should().Contain("verbose_json");
    }

    [Fact]
    public void Only_the_segments_that_are_speech_are_kept()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""
            {"text":"bán bún bò ... cảm ơn các bạn đã theo dõi",
             "segments":[{"text":" bán bún bò","no_speech_prob":0.05},
                         {"text":" ...","no_speech_prob":0.91},
                         {"text":" Cảm ơn các bạn đã theo dõi","no_speech_prob":0.3}]}
            """);

        GroqWhisperSpeechToText.SpokenText(doc.RootElement).Should().Be("bán bún bò");
    }

    [Fact]
    public async Task Speech_to_text_moves_to_the_next_key_when_one_is_rate_limited()
    {
        var handler = new Recorder((req, _) => req.Headers.Authorization!.Parameter == "gsk_test_key_one" || req.Headers.Authorization!.Parameter == "gsk_test_key_two"
            ? (handlerCalls++ == 0 ? Json((HttpStatusCode)429, """{"error":{"message":"rate"}}""") : Json(HttpStatusCode.OK, """{"text":"hai người"}"""))
            : Json(HttpStatusCode.Unauthorized, "{}"));
        var stt = new GroqWhisperSpeechToText(new HttpClient(handler), Config(), new AiKeyPools(Config()), NullLogger<GroqWhisperSpeechToText>.Instance);

        (await stt.TranscribeAsync([1], "audio/wav", CancellationToken.None)).Text.Should().Be("hai người");
        handler.Calls.Should().HaveCount(2);
        handler.Calls.Select(c => c.Request.Headers.Authorization!.Parameter).Distinct().Should().HaveCount(2);
    }

    private int handlerCalls;

    [Fact]
    public async Task Speech_to_text_with_every_key_failing_is_a_503_for_the_client()
    {
        var handler = new Recorder((_, _) => Json(HttpStatusCode.ServiceUnavailable, "{}"));
        var stt = new GroqWhisperSpeechToText(new HttpClient(handler), Config(), new AiKeyPools(Config()), NullLogger<GroqWhisperSpeechToText>.Instance);

        await FluentActions.Awaiting(() => stt.TranscribeAsync([1], "audio/wav", CancellationToken.None))
            .Should().ThrowAsync<AiUnavailableException>();
    }

    [Fact]
    public async Task A_clip_the_provider_rejects_is_not_retried_on_other_keys()
    {
        var handler = new Recorder((_, _) => Json(HttpStatusCode.BadRequest, """{"error":{"message":"bad audio"}}"""));
        var stt = new GroqWhisperSpeechToText(new HttpClient(handler), Config(), new AiKeyPools(Config()), NullLogger<GroqWhisperSpeechToText>.Instance);

        await FluentActions.Awaiting(() => stt.TranscribeAsync([1], "audio/wav", CancellationToken.None))
            .Should().ThrowAsync<AiUnavailableException>();
        handler.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task Groq_extraction_asks_for_the_strict_schema_and_normalises_values()
    {
        var handler = new Recorder((_, _) => Json(HttpStatusCode.OK,
            new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["content"] = ExtractionJson } }) }.ToJsonString()));
        var groq = new GroqRegistrationExtractor(new HttpClient(handler), Config(), new AiKeyPools(Config()), NullLogger<GroqRegistrationExtractor>.Instance);

        var result = await groq.ExtractAsync(Input, CancellationToken.None);

        result!.Fields.Single(f => f.Field == AssistFields.CapitalAmount).Value.Should().Be("30000000");
        result.Fields.Single(f => f.Field == AssistFields.ContactSameAsPermanent).Value.Should().Be("false");
        result.PromptVersion.Should().Be(RegistrationExtractionPrompt.Version);
        var request = JsonNode.Parse(handler.Calls.Single().Body)!;
        request["response_format"]!["type"]!.GetValue<string>().Should().Be("json_schema");
        request["response_format"]!["json_schema"]!["strict"]!.GetValue<bool>().Should().BeTrue();
        request["messages"]![1]!["content"]!.GetValue<string>().Should().Be("Vốn bỏ ra chừng ba chục triệu");
    }

    [Fact]
    public async Task Gemini_extraction_sends_a_system_instruction_a_schema_and_the_clip_for_one_hop()
    {
        var handler = new Recorder((_, _) => Json(HttpStatusCode.OK,
            new JsonObject
            {
                ["candidates"] = new JsonArray(new JsonObject
                {
                    ["content"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = ExtractionJson.Replace("{\"businessLine\"", "{\"transcript\":\"Vốn bỏ ra chừng ba chục triệu\",\"businessLine\"") }) },
                }),
            }.ToJsonString()));
        var config = Config();
        var gemini = new GeminiRegistrationExtractor(
            new GeminiVisionClient(new HttpClient(handler), config, NullLogger<GeminiVisionClient>.Instance, new AiKeyPools(config)), config);

        var result = await gemini.ExtractAsync(Input with { Transcript = null, Audio = [9, 9], AudioMimeType = "audio/wav" }, CancellationToken.None);

        result!.Transcript.Should().Be("Vốn bỏ ra chừng ba chục triệu");
        var request = JsonNode.Parse(handler.Calls.First().Body)!;
        request["systemInstruction"]!["parts"]![0]!["text"]!.GetValue<string>().Should().Contain("Câu trợ lý vừa hỏi (mã CAPITAL)");
        request["generationConfig"]!["responseSchema"]!["properties"]!["transcript"].Should().NotBeNull();
        request["contents"]![0]!["parts"]![0]!["inline_data"]!["mime_type"]!.GetValue<string>().Should().Be("audio/wav");
        handler.Calls.First().Request.RequestUri!.ToString().Should().Contain(GeminiRegistrationExtractor.DefaultModels[0]);
    }

    [Fact]
    public async Task A_model_that_rejects_the_thinking_setting_with_a_bare_400_is_asked_again_without_it()
    {
        var handler = new Recorder((_, body) => body.Contains("thinkingConfig")
            ? Json(HttpStatusCode.BadRequest, """{"error":{"message":"Request contains an invalid argument."}}""")
            : Json(HttpStatusCode.OK, new JsonObject { ["candidates"] = new JsonArray(new JsonObject { ["content"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = ExtractionJson }) } }) }.ToJsonString()));
        var config = Config();
        var gemini = new GeminiRegistrationExtractor(
            new GeminiVisionClient(new HttpClient(handler), config, NullLogger<GeminiVisionClient>.Instance, new AiKeyPools(config)), config);

        var result = await gemini.ExtractAsync(Input, CancellationToken.None);

        result.Should().NotBeNull();
        handler.Calls.Should().HaveCount(2);
        handler.Calls[1].Body.Should().NotContain("thinkingConfig");
    }

    [Fact]
    public void The_prompt_carries_only_the_non_sensitive_draft()
    {
        var prompt = RegistrationExtractionPrompt.System(Input);
        prompt.Should().Contain("\"BusinessLine\":\"Bún bò\"");
        prompt.Should().NotContain("Salutation").And.NotContain("Attempts");
        prompt.Should().Contain("22/10/2026");
    }

    [Fact]
    public async Task The_composite_fails_over_to_the_next_extractor_and_only_gemini_hears_audio()
    {
        var groqHandler = new Recorder((_, _) => Json(HttpStatusCode.ServiceUnavailable, "{}"));
        var geminiHandler = new Recorder((_, _) => Json(HttpStatusCode.OK,
            new JsonObject { ["candidates"] = new JsonArray(new JsonObject { ["content"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = ExtractionJson }) } }) }.ToJsonString()));
        var config = Config(("AiCompliance:VoiceRegistration:Extractors:0", "groq"), ("AiCompliance:VoiceRegistration:Extractors:1", "gemini"));
        var pools = new AiKeyPools(config);
        var composite = new CompositeRegistrationExtractor(
            new GeminiRegistrationExtractor(new GeminiVisionClient(new HttpClient(geminiHandler), config, NullLogger<GeminiVisionClient>.Instance, pools), config),
            new GroqRegistrationExtractor(new HttpClient(groqHandler), config, pools, NullLogger<GroqRegistrationExtractor>.Instance),
            config,
            NullLogger<CompositeRegistrationExtractor>.Instance);

        (await composite.ExtractAsync(Input, CancellationToken.None)).Model.Should().Be("gemini");
        composite.AcceptsAudio.Should().BeTrue();

        groqHandler.Calls.Clear();
        await composite.ExtractAsync(Input with { Transcript = null, Audio = [1], AudioMimeType = "audio/wav" }, CancellationToken.None);
        groqHandler.Calls.Should().BeEmpty("Groq's chat models cannot take audio");
    }

    [Fact]
    public async Task With_every_extractor_failing_the_composite_is_a_503()
    {
        var down = new Recorder((_, _) => Json(HttpStatusCode.ServiceUnavailable, "{}"));
        var config = Config();
        var pools = new AiKeyPools(config);
        var composite = new CompositeRegistrationExtractor(
            new GeminiRegistrationExtractor(new GeminiVisionClient(new HttpClient(down), config, NullLogger<GeminiVisionClient>.Instance, pools), config),
            new GroqRegistrationExtractor(new HttpClient(down), config, pools, NullLogger<GroqRegistrationExtractor>.Instance),
            config,
            NullLogger<CompositeRegistrationExtractor>.Instance);

        await FluentActions.Awaiting(() => composite.ExtractAsync(Input, CancellationToken.None)).Should().ThrowAsync<AiUnavailableException>();
    }
}
