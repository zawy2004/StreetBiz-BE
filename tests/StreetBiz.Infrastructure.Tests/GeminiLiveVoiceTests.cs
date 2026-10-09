using System.Text.Json;
using StreetBiz.Application.Features.Chatbot;
using StreetBiz.Infrastructure.Services.Chatbot;

namespace StreetBiz.Infrastructure.Tests;

public sealed class GeminiLiveVoiceTests
{
    [Fact]
    public void Setup_requests_audio_with_both_transcriptions_and_wire_safe_tool_names()
    {
        var schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { }, additionalProperties = false });
        var setup = GeminiLiveVoiceProvider.Setup(new() { Model = "live-model", VoiceName = "Kore", SilenceDurationMs = 700 },
            new("Bạn là Trợ lý StreetBiz.", [new("vendor.finance", "Tài chính của tôi", schema)]))["setup"]!;
        Assert.Equal("models/live-model", setup["model"]!.GetValue<string>());
        Assert.Equal("AUDIO", setup["generationConfig"]!["responseModalities"]![0]!.GetValue<string>());
        Assert.Equal("Kore", setup["generationConfig"]!["speechConfig"]!["voiceConfig"]!["prebuiltVoiceConfig"]!["voiceName"]!.GetValue<string>());
        Assert.NotNull(setup["inputAudioTranscription"]); Assert.NotNull(setup["outputAudioTranscription"]);
        Assert.Equal(700, setup["realtimeInputConfig"]!["automaticActivityDetection"]!["silenceDurationMs"]!.GetValue<int>());
        Assert.Equal("vendor__finance", setup["tools"]![0]!["functionDeclarations"]![0]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void Server_messages_map_to_audio_captions_interruptions_tools_and_usage()
    {
        var audio = Convert.ToBase64String([1, 2, 3, 4]);
        var events = Parse($$$"""
            {"serverContent":{"interrupted":true,"inputTranscription":{"text":"Ô HC-08"},
              "modelTurn":{"parts":[{"inlineData":{"mimeType":"audio/pcm;rate=24000","data":"{{{audio}}}"}},{"text":"ignored"}]},
              "outputTranscription":{"text":"Để mình kiểm tra"},"turnComplete":true},
             "usageMetadata":{"promptTokenCount":120,"responseTokenCount":30}}
            """);
        Assert.IsType<ChatbotVoiceInterrupted>(events[0]);
        Assert.Equal("Ô HC-08", Assert.IsType<ChatbotVoiceInputText>(events[1]).Text);
        Assert.Equal([1, 2, 3, 4], Assert.IsType<ChatbotVoiceAudio>(events[2]).Pcm);
        Assert.Equal("Để mình kiểm tra", Assert.IsType<ChatbotVoiceOutputText>(events[3]).Text);
        Assert.IsType<ChatbotVoiceTurnComplete>(events[4]);
        Assert.Equal(new ChatbotUsage(120, 30), Assert.IsType<ChatbotVoiceUsage>(events[5]).Usage);

        var call = Assert.Single(Assert.IsType<ChatbotVoiceToolCalls>(Assert.Single(Parse(
            """{"toolCall":{"functionCalls":[{"id":"c1","name":"ward__slot_permit","args":{"slotCode":"HC-08"}}]}}"""))).Calls);
        Assert.Equal(("c1", "ward.slot_permit"), (call.Id, call.Name));
        Assert.Equal("HC-08", JsonDocument.Parse(call.Arguments).RootElement.GetProperty("slotCode").GetString());
        Assert.Equal(["c1"], Assert.IsType<ChatbotVoiceToolCancelled>(Assert.Single(Parse("""{"toolCallCancellation":{"ids":["c1"]}}"""))).Ids);
        Assert.IsType<ChatbotVoiceGoAway>(Assert.Single(Parse("""{"goAway":{"timeLeft":"10s"}}""")));
        Assert.Empty(Parse("""{"setupComplete":{}}"""));
    }

    private static List<ChatbotVoiceEvent> Parse(string json) => GeminiLiveUpstream.Map(JsonDocument.Parse(json).RootElement).ToList();
}
