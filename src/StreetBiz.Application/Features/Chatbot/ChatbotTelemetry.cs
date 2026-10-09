using System.Diagnostics.Metrics;

namespace StreetBiz.Application.Features.Chatbot;

/// <summary>No question, user ID, credentials, raw exceptions, or record content in metric labels.</summary>
public static class ChatbotTelemetry
{
    public static readonly Meter Meter = new("StreetBiz.Chatbot", "1.0");
    public static readonly Histogram<double> TurnSeconds = Meter.CreateHistogram<double>("chatbot.turn.seconds", "s");
    public static readonly Histogram<double> FirstDeltaSeconds = Meter.CreateHistogram<double>("chatbot.first_delta.seconds", "s");
    public static readonly Counter<long> Tokens = Meter.CreateCounter<long>("chatbot.reserved_tokens");
    public static readonly Counter<long> Fallbacks = Meter.CreateCounter<long>("chatbot.fallbacks");
    public static readonly Counter<long> Turns = Meter.CreateCounter<long>("chatbot.turns");
    public static readonly Counter<long> ProviderErrors = Meter.CreateCounter<long>("chatbot.provider.errors");
    public static readonly Histogram<double> VoiceReplySeconds = Meter.CreateHistogram<double>("chatbot.voice.reply.seconds", "s");
    public static readonly Histogram<double> VoiceSessionSeconds = Meter.CreateHistogram<double>("chatbot.voice.session.seconds", "s");
    public static readonly Counter<long> VoiceTokens = Meter.CreateCounter<long>("chatbot.voice.tokens");
    public static readonly Counter<long> VoiceReconnects = Meter.CreateCounter<long>("chatbot.voice.reconnects");
}
