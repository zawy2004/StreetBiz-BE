using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StreetBiz.Application.Features.Chatbot;
using StreetBiz.Infrastructure.Persistence.Repositories;

namespace StreetBiz.Infrastructure.Services.Chatbot;

public static class ChatbotRegistration
{
    public static IServiceCollection AddChatbotInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var settings = new ChatbotSettings();
        config.GetSection("Chatbot").Bind(settings);
        if (settings.MaxQuestionCharacters is < 100 or > 10000 || settings.MaxOutputTokens is < 128 or > 8192
            || settings.TurnTimeoutSeconds is < 10 or > 120 || settings.GlobalConcurrentTurns is < 1 or > 100
            || settings.ProviderTimeoutSeconds is < 5 or > 90 || settings.ProviderTimeoutSeconds > settings.TurnTimeoutSeconds
            || settings.ImageProviderTimeoutSeconds is < 5 or > 60
            || settings.MaxToolCalls is < 1 or > 10 || settings.MaxModelSteps is < 2 or > 8
            || settings.RetentionDays is < 1 or > 365 || settings.MaxTurnTokenBudget is < 8000 or > 100000
            || settings.DailyTokenBudget < settings.MaxTurnTokenBudget || settings.MaxContextCharacters is < 2000 or > 40000)
            throw new InvalidOperationException("Chatbot settings exceed supported limits.");
        services.AddSingleton(settings);
        var voice = new ChatbotVoiceSettings();
        config.GetSection("Chatbot:Voice").Bind(voice);
        if (voice.MaxSessionSeconds is < 30 or > 900 || voice.DailySecondsPerUser is < 60 or > 14400 || voice.IdleSeconds is < 20 or > 300
            || voice.GlobalConcurrentSessions is < 1 or > 50 || voice.SilenceDurationMs is < 200 or > 3000 || voice.MaxToolCalls is < 1 or > 30
            || voice.TicketSeconds is < 5 or > 120 || voice.MaxReconnects is < 0 or > 5 || (voice.Enabled && string.IsNullOrWhiteSpace(voice.Model)))
            throw new InvalidOperationException("Chatbot voice settings exceed supported limits.");
        services.AddSingleton(voice);
        services.AddSingleton<ChatbotVoiceSessions>();
        services.AddSingleton<IChatbotVoiceProvider, GeminiLiveVoiceProvider>();
        services.AddScoped<ChatbotVoiceService>();
        services.AddSingleton<ChatbotKnowledge>();
        services.AddSingleton<ChatbotRuntime>();
        services.AddSingleton<ChatbotProviderHealth>();
        services.AddScoped<IChatbotActorResolver, ChatbotActorResolver>();
        services.AddScoped<IChatbotStore, ChatbotStore>();
        services.AddScoped<ChatbotTools>();
        services.AddScoped<IChatbotListReader, ChatbotListReader>();
        services.AddScoped<ChatbotService>();
        services.AddSingleton<IChatbotAttachments, ChatbotAttachmentService>();
        services.AddScoped<IChatbotPublicImages, ChatbotPublicImages>();
        services.AddHostedService<ChatbotRetentionService>();
        services.AddHttpClient<GroqChatbotModel>(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .RedactLoggedHeaders(["Authorization"]);
        services.AddHttpClient<GeminiChatbotModel>(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .RedactLoggedHeaders(["x-goog-api-key"]);
        services.AddTransient<IChatbotModel>(sp => sp.GetRequiredService<GroqChatbotModel>());
        services.AddTransient<IChatbotModel>(sp => sp.GetRequiredService<GeminiChatbotModel>());
        return services;
    }
}

