using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using StreetBiz.Application.Features.Chatbot;
using StreetBiz.Infrastructure.Services;
using StreetBiz.Infrastructure.Services.Chatbot;

namespace StreetBiz.Infrastructure.Tests;

public sealed class ChatbotProviderTests
{
    private static IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
        ["AiCompliance:Groq:ApiKey"] = "test-placeholder", ["AiCompliance:Groq:Model"] = "openai/gpt-oss-120b",
        ["AiCompliance:Gemini:ApiKey"] = "test-placeholder", ["AiCompliance:Gemini:Model"] = "test-gemini"
    }).Build();
    private static ChatbotProviderRequest Request(bool final = true) => new("system", [], "Xin chào", [], [], [], final);

    [Fact]
    public async Task Groq_stream_uses_final_usage_and_never_exposes_reasoning()
    {
        var wire = "data: {\"choices\":[{\"delta\":{\"reasoning\":\"hidden\",\"content\":\"Xin \"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"chào\"},\"finish_reason\":\"stop\"}]}\n\n" +
            "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":4}}\n\ndata: [DONE]\n\n";
        var handler = new Handler(wire); var config = Config();
        var model = new GroqChatbotModel(new(handler), config, new(config), new(), new(TimeProvider.System));
        var deltas = new StringBuilder();
        var result = await model.GenerateAsync(Request(), (text, _) => { deltas.Append(text); return Task.CompletedTask; }, default);
        Assert.Equal("Xin chào", deltas.ToString()); Assert.Equal(12, result.Usage.InputTokens);
        Assert.Contains("\"include_reasoning\":false", handler.Body);
        Assert.Contains("\"include_usage\":true", handler.Body);
        Assert.DoesNotContain("test-placeholder", handler.Url);
    }

    [Fact]
    public async Task Missing_terminal_frame_is_not_a_successful_answer()
    {
        var config = Config(); var handler = new Handler("data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\n");
        var model = new GroqChatbotModel(new(handler), config, new(config), new(), new(TimeProvider.System));
        Assert.Equal("incomplete_response", (await Assert.ThrowsAsync<ChatbotProviderException>(() => model.GenerateAsync(Request(), (_, _) => Task.CompletedTask, default))).Category);
    }

    [Fact]
    public async Task Quota_failure_is_transient_and_does_not_rotate_keys_or_leak_body()
    {
        var config = Config(); var handler = new Handler("provider internal test-only error", HttpStatusCode.TooManyRequests);
        var model = new GroqChatbotModel(new(handler), config, new(config), new(), new(TimeProvider.System));
        var error = await Assert.ThrowsAsync<ChatbotProviderException>(() => model.GenerateAsync(Request(), (_, _) => Task.CompletedTask, default));
        Assert.True(error.Transient); Assert.Equal(1, handler.Calls); Assert.DoesNotContain("internal", error.Message);
    }

    [Fact]
    public async Task Gemini_preserves_thought_signatures_and_native_call_ids()
    {
        var config = Config();
        var native = JsonDocument.Parse("""{"role":"model","parts":[{"thoughtSignature":"opaque-signature","functionCall":{"id":"call-1","name":"help__search","args":{}}}]}""").RootElement.Clone();
        var handler = new Handler("""{"candidates":[{"content":{"role":"model","parts":[{"text":"ok"}]},"finishReason":"STOP"}]}""");
        var model = new GeminiChatbotModel(new(handler), config, new(config), new(), new(TimeProvider.System));
        await model.GenerateAsync(Request(false) with { Exchanges = [new("GEMINI", native, [new("call-1", "help.search", "{}")])] }, (_, _) => Task.CompletedTask, default);
        using var body = JsonDocument.Parse(handler.Body!);
        var contents = body.RootElement.GetProperty("contents");
        Assert.Equal("opaque-signature", contents[1].GetProperty("parts")[0].GetProperty("thoughtSignature").GetString());
        Assert.Equal("call-1", contents[2].GetProperty("parts")[0].GetProperty("functionResponse").GetProperty("id").GetString());
    }

    [Fact]
    public async Task Gemini_stream_filters_thought_parts()
    {
        var config = Config(); var handler = new Handler("""data: {"candidates":[{"content":{"parts":[{"text":"hidden","thought":true},{"text":"Kết quả"}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":8,"candidatesTokenCount":3,"thoughtsTokenCount":2}}""" + "\n\n");
        var model = new GeminiChatbotModel(new(handler), config, new(config), new(), new(TimeProvider.System));
        var result = await model.GenerateAsync(Request(), (_, _) => Task.CompletedTask, default);
        Assert.Equal("Kết quả", result.Text); Assert.Equal(5, result.Usage.OutputTokens);
    }

    [Fact]
    public async Task Sse_rejects_oversized_lines_and_accepts_comments_crlf_and_multiline_data()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(": keepalive\r\ndata: {\r\ndata: \"ok\":true}\r\n\r\ndata: [DONE]\r\n\r\n"));
        var frames = new List<JsonElement>(); await foreach (var f in ChatbotHttpModel.ReadSseAsync(stream, default)) frames.Add(f);
        Assert.True(Assert.Single(frames).GetProperty("ok").GetBoolean());
        using var big = new MemoryStream(Encoding.UTF8.GetBytes("data: " + new string('x', 262145)));
        await Assert.ThrowsAsync<ChatbotProviderException>(async () => { await foreach (var _ in ChatbotHttpModel.ReadSseAsync(big, default)) { } });
    }

    private sealed class Handler(string response, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string? Body; public string? Url; public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; Body = await request.Content!.ReadAsStringAsync(ct); Url = request.RequestUri!.ToString();
            return new(status) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Independent_quota_failover_is_bounded_and_failed_key_is_cooled_down(bool gemini, bool independent)
    {
        var provider = gemini ? "Gemini" : "Groq";
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            [$"AiCompliance:{provider}:ApiKeys:0"] = "test-first", [$"AiCompliance:{provider}:ApiKeys:1"] = "test-second",
            [$"AiCompliance:{provider}:Model"] = "test"
        }).Build();
        var handler = new RotationHandler(gemini);
        var settings = new ChatbotSettings { IndependentKeyQuotas = independent };
        IChatbotModel model = gemini
            ? new GeminiChatbotModel(new(handler), config, new(config), settings, new(TimeProvider.System))
            : new GroqChatbotModel(new(handler), config, new(config), settings, new(TimeProvider.System));
        if (independent)
        {
            await model.GenerateAsync(Request(false), (_, _) => Task.CompletedTask, default);
            await model.GenerateAsync(Request(false), (_, _) => Task.CompletedTask, default);
            Assert.Equal(new[] { "test-first", "test-second", "test-second" }, handler.Keys);
        }
        else
        {
            Assert.Equal("provider_quota", (await Assert.ThrowsAsync<ChatbotProviderException>(() => model.GenerateAsync(Request(false), (_, _) => Task.CompletedTask, default))).Category);
            await Assert.ThrowsAsync<ChatbotProviderException>(() => model.GenerateAsync(Request(false), (_, _) => Task.CompletedTask, default));
            Assert.Single(handler.Keys);
        }
    }

    [Theory]
    [InlineData("MAX_TOKENS", "output_limit")]
    [InlineData("SAFETY", "blocked_response")]
    [InlineData("UNEXPECTED_TOOL_CALL", "unexpected_tool_call")]
    [InlineData("MALFORMED_FUNCTION_CALL", "malformed_function_call")]
    public async Task Gemini_classifies_finish_errors(string finish, string expected)
    {
        var config = Config(); var handler = new Handler("{\"candidates\":[{\"finishReason\":\"" + finish + "\"}]}");
        var model = new GeminiChatbotModel(new(handler), config, new(config), new(), new(TimeProvider.System));
        Assert.Equal(expected, (await Assert.ThrowsAsync<ChatbotProviderException>(() => model.GenerateAsync(Request(false), (_, _) => Task.CompletedTask, default))).Category);
    }

    [Theory]
    [InlineData("STOP", null)]
    [InlineData("MAX_TOKENS", "output_limit")]
    [InlineData("UNEXPECTED_TOOL_CALL", "unexpected_tool_call")]
    [InlineData("SAFETY", "blocked_response")]
    [InlineData(null, "incomplete_response")]
    public async Task Vision_final_validates_json_before_publishing_text(string? finish, string? error)
    {
        var config = Config();
        var candidate = new Dictionary<string, object> { ["content"] = new { parts = new[] { new { text = "Ảnh có thể là bánh mì." } } } };
        if (finish is not null) candidate["finishReason"] = finish;
        var handler = new Handler(JsonSerializer.Serialize(new { candidates = new[] { candidate } }));
        var model = new GeminiChatbotModel(new(handler), config, new(config), new(), new(TimeProvider.System));
        var chunks = new List<string>(); var request = Request() with { Images = [new([1, 2], "image/png")] };
        if (error is null)
        {
            var result = await model.GenerateAsync(request, (chunk, _) => { chunks.Add(chunk); return Task.CompletedTask; }, default);
            Assert.Equal(result.Text, Assert.Single(chunks));
        }
        else
        {
            var failure = await Assert.ThrowsAsync<ChatbotProviderException>(() => model.GenerateAsync(request,
                (chunk, _) => { chunks.Add(chunk); return Task.CompletedTask; }, default));
            Assert.Equal(error, failure.Category); Assert.Empty(chunks);
        }
        Assert.EndsWith(":generateContent", handler.Url);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("NONE", body.RootElement.GetProperty("toolConfig").GetProperty("functionCallingConfig").GetProperty("mode").GetString());
    }

    [Theory]
    [InlineData(false, null, "gemini-3.5-flash-lite", "minimal")]
    [InlineData(true, null, "test-gemini", null)]
    [InlineData(false, "gemini-3.8-flash", "gemini-3.8-flash", "low")]
    public async Task Food_model_routing_keeps_complex_analysis_on_main_model(bool complex, string? configured, string expected, string? thinking)
    {
        var config = new ConfigurationBuilder().AddConfiguration(Config()).AddInMemoryCollection(
            new Dictionary<string, string?> { ["Chatbot:Gemini:FoodVisionModel"] = configured }).Build();
        var handler = new Handler("""{"candidates":[{"content":{"parts":[{"functionCall":{"name":"public__food","args":{"query":"bánh mì"}}}]},"finishReason":"STOP"}]}""");
        var model = new GeminiChatbotModel(new(handler), config, new(config), new(), new(TimeProvider.System));
        var result = await model.GenerateAsync(Request(false) with { Complex = complex, Images = [new([1], "image/png")],
            Tools = [new("public.food", "public food lookup", JsonSerializer.SerializeToElement(new { type = "object" }))] }, (_, _) => Task.CompletedTask, default);
        Assert.Equal(expected, result.Model); Assert.Contains($"models/{expected}:", handler.Url);
        using var payload = JsonDocument.Parse(handler.Body!);
        if (thinking is not null) Assert.Equal(thinking, payload.RootElement.GetProperty("generationConfig").GetProperty("thinkingConfig").GetProperty("thinkingLevel").GetString());
        Assert.Equal("public.food", Assert.Single(result.ToolCalls).Name);
    }

    [Fact]
    public async Task Missing_optional_food_model_falls_back_to_configured_model()
    {
        var config = Config(); var handler = new FoodModelFallbackHandler();
        var model = new GeminiChatbotModel(new(handler), config, new(config), new(), new(TimeProvider.System));
        var request = Request(false) with { Images = [new([1], "image/png")],
            Tools = [new("public.food", "public food lookup", JsonSerializer.SerializeToElement(new { type = "object" }))] };
        var result = await model.GenerateAsync(request, (_, _) => Task.CompletedTask, default);
        Assert.Equal("test-gemini", result.Model);
        Assert.Equal(new[] { "gemini-3.5-flash-lite", "test-gemini" }, handler.Models);
    }

    private sealed class FoodModelFallbackHandler : HttpMessageHandler
    {
        public List<string> Models { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var model = request.RequestUri!.AbsolutePath.Split('/').Last().Split(':')[0]; Models.Add(model);
            return Task.FromResult(new HttpResponseMessage(Models.Count == 1 ? HttpStatusCode.NotFound : HttpStatusCode.OK) {
                Content = new StringContent("""{"candidates":[{"content":{"parts":[{"text":"Cần ảnh rõ hơn."}]},"finishReason":"STOP"}]}""") });
        }
    }

    private sealed class RotationHandler(bool gemini) : HttpMessageHandler
    {
        public readonly List<string> Keys = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var key = gemini ? request.Headers.GetValues("x-goog-api-key").Single() : request.Headers.Authorization!.Parameter!;
            Keys.Add(key);
            return Task.FromResult(key == "test-first"
                ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(gemini
                    ? """{"candidates":[{"content":{"parts":[{"text":"ok"}]},"finishReason":"STOP"}]}"""
                    : """{"choices":[{"message":{"content":"ok"},"finish_reason":"stop"}]}""") });
        }
    }
}
