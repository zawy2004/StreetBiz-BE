using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StreetBiz.Application.Features.Chatbot;
using StreetBiz.Infrastructure.Services;
using StreetBiz.Infrastructure.Services.Chatbot;

namespace StreetBiz.API.Tests;

// Explicit opt-in: uses LOCAL development SQL/configuration, no business changes, no seed.
public sealed class LocalChatbotFactAttribute : FactAttribute
{
    public LocalChatbotFactAttribute() { if (Environment.GetEnvironmentVariable("STREETBIZ_CHATBOT_LIVE_TEST") != "1") Skip = "Opt-in local SQL/provider smoke test."; }
}

public sealed class ChatbotLiveTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static string ApiRoot
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                var api = Path.Combine(directory.FullName, "src", "StreetBiz.API");
                if (File.Exists(Path.Combine(api, "StreetBiz.API.csproj"))) return api;
            }
            throw new DirectoryNotFoundException("API project was not found.");
        }
    }
    private static IConfiguration Configuration() => new ConfigurationBuilder().SetBasePath(ApiRoot)
        .AddJsonFile("appsettings.json").AddJsonFile("appsettings.Development.json", true)
        .AddUserSecrets("streetbiz-api-development").Build();

    [LocalChatbotFact]
    public async Task Local_sql_all_roles_read_only_tools_idempotency_and_ownership()
    {
        var configuration = Configuration();
        var connection = configuration.GetConnectionString("StreetBizDB") ?? configuration.GetConnectionString("StreetBizDatabase");
        var target = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connection);
        Assert.Equal("localhost", target.DataSource); Assert.Equal("StreetBizDB", target.InitialCatalog);
        using var host = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:StreetBizDB", connection);
            builder.UseSetting("Jwt:SigningKey", configuration["Jwt:SigningKey"]);
            builder.ConfigureAppConfiguration((_, c) => c.AddConfiguration(configuration).AddInMemoryCollection(new Dictionary<string, string?> {
                ["Chatbot:Groq:Model"] = "", ["Chatbot:Gemini:Model"] = "", // no paid calls or private records sent externally
            }));
            builder.ConfigureServices(services =>
            {
                foreach (var service in services.Where(s => s.ServiceType == typeof(IHostedService) &&
                    (s.ImplementationType == typeof(FeeReminderHostedService) || s.ImplementationType == typeof(ChatbotRetentionService))).ToArray()) services.Remove(service);
            });
        });
        string? previousConversation = null;
        foreach (var (phone, role, question) in new[] {
            ("0905000101", "VENDOR", "Hồ sơ kinh doanh của tôi đang ở bước nào?"),
            ("0983000001", "WARD_AUTHORITY", "Tóm tắt tình hình phường"),
            ("0900000001", "PLATFORM_ADMIN", "Các danh mục hiện có"),
            ("0905000201", "CUSTOMER", "Thông báo của tôi"),
        })
        {
            using var client = host.CreateClient();
            // Repository-documented fictional demo account, never a real user's credentials.
            var login = await client.PostAsJsonAsync("/api/auth/login", new { phoneNumber = phone, password = "Password123!" });
            Assert.True(login.IsSuccessStatusCode, $"Demo login failed for {role}: {(int)login.StatusCode}");
            var auth = await login.Content.ReadFromJsonAsync<JsonElement>();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.GetProperty("accessToken").GetString());
            string? conversation = null;
            try
            {
                if (previousConversation != null) Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/chatbot/conversations/{previousConversation}/messages")).StatusCode);
                var create = await client.PostAsJsonAsync("/api/chatbot/conversations", new { clientRequestId = Guid.NewGuid().ToString() });
                Assert.Equal(HttpStatusCode.Created, create.StatusCode);
                conversation = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("id").GetString()!;
                var request = new ChatbotSendRequest(Guid.NewGuid().ToString(), question);
                var sent = await client.PostAsJsonAsync($"/api/chatbot/conversations/{conversation}/messages", request);
                Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
                var answer = (await sent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("assistantMessage");
                Assert.Equal("COMPLETED", answer.GetProperty("status").GetString());
                Assert.Contains(answer.GetProperty("sources").EnumerateArray(), s => s.GetProperty("kind").GetString() == "LIVE_DATA");
                var replay = await client.PostAsJsonAsync($"/api/chatbot/conversations/{conversation}/messages", request);
                Assert.Equal(answer.GetProperty("id").GetString(), (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("assistantMessage").GetProperty("id").GetString());
                if (role == "VENDOR")
                {
                    foreach (var followup in new[] { "Tôi cần đóng khoản phí nào?", "Hợp đồng của tôi khi nào hết hạn?" })
                    {
                        var next = await client.PostAsJsonAsync($"/api/chatbot/conversations/{conversation}/messages", new ChatbotSendRequest(Guid.NewGuid().ToString(), followup));
                        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
                        var message = (await next.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("assistantMessage");
                        Assert.Equal("COMPLETED", message.GetProperty("status").GetString());
                        Assert.Contains(message.GetProperty("sources").EnumerateArray(), s => s.GetProperty("kind").GetString() == "LIVE_DATA");
                    }
                }
                previousConversation = conversation;
            }
            finally
            {
                if (conversation != null) await client.DeleteAsync($"/api/chatbot/conversations/{conversation}");
                await client.PostAsync("/api/auth/logout", null);
            }
        }
    }

    [LocalChatbotFact]
    public async Task Configured_providers_stream_synthetic_public_question()
    {
        var config = Configuration(); var settings = new ChatbotSettings { MaxOutputTokens = 1024, IndependentKeyQuotas = true, ProviderTimeoutSeconds = 55 };
        var keys = new AiKeyPools(config); var health = new ChatbotProviderHealth(TimeProvider.System);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        IChatbotModel[] models = [new GroqChatbotModel(http, config, keys, settings, health), new GeminiChatbotModel(http, config, keys, settings, health)];
        foreach (var model in models)
        {
            Assert.True(model.Available, $"{model.Name} is not configured.");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var request = new ChatbotProviderRequest("Trả lời ngắn bằng tiếng Việt. Đây là kiểm thử kỹ thuật với dữ liệu giả lập.", [], "Nói lời chào trong một câu, không gọi công cụ.", [], [], [], true);
            var result = await model.GenerateAsync(request, (_, _) => Task.CompletedTask, deadline.Token);
            Assert.False(string.IsNullOrWhiteSpace(result.Text), $"No streamed text from {model.Name}.");
        }
    }

    [LocalChatbotFact]
    public async Task Configured_vision_recognizes_bread_in_one_call()
    {
        var path = Path.GetFullPath(Path.Combine(ApiRoot, "../../../StreetBiz-FE/public/images/food/banh-mi.jpg"));
        var bytes = await File.ReadAllBytesAsync(path);
        try
        {
            var config = Configuration(); var settings = new ChatbotSettings { IndependentKeyQuotas = true };
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            var model = new GeminiChatbotModel(http, config, new(config), settings, new(TimeProvider.System));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(settings.ImageProviderTimeoutSeconds));
            var schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { query = new { type = "string" } }, required = new[] { "query" } });
            var request = new ChatbotProviderRequest(ChatbotKnowledge.SystemPrompt(new(1, 1, "CUSTOMER", null, null)), [],
                "Tôi muốn ăn món như hình, tìm quầy trong web giúp tôi.",
                [new("public.food", "Tìm món/quầy theo tên món phổ biến nhìn thấy trong ảnh", schema)], [], [], false, false, [new(bytes, "image/jpeg")]);
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            var result = await model.GenerateAsync(request, (_, _) => Task.CompletedTask, deadline.Token);
            output.WriteLine("Public bread photo: {0}, one model call, {1:F2} seconds.", result.Model, elapsed.Elapsed.TotalSeconds);
            var call = Assert.Single(result.ToolCalls); Assert.Equal("public.food", call.Name);
            var query = ChatbotJson.Read<JsonElement>(call.Arguments).GetProperty("query").GetString()!;
            Assert.StartsWith("banh mi", ChatbotKnowledge.Normalize(query));
            Assert.True(result.Usage.InputTokens > 0);
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
    }

    [LocalChatbotFact]
    public async Task Configured_vision_accepts_public_demo_photo()
    {
        // Public illustrative asset bundled with the FE, never a user's upload or private record.
        var path = Path.GetFullPath(Path.Combine(ApiRoot, "../../../StreetBiz-FE/public/images/food/bun-cha.jpg"));
        var bytes = await File.ReadAllBytesAsync(path);
        try
        {
            var config = Configuration();
            var settings = new ChatbotSettings { MaxOutputTokens = 1024, IndependentKeyQuotas = true, ProviderTimeoutSeconds = 55 };
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(55) };
            var model = new GeminiChatbotModel(http, config, new(config), settings, new(TimeProvider.System));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(55));
            var schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { query = new { type = "string" } }, required = new[] { "query" } });
            var request = new ChatbotProviderRequest("Đây là kiểm thử bằng ảnh minh họa công khai. Trước khi trả lời nơi bán, phải gọi public.food để tra cứu; không tự bịa quầy.", [],
                "Tôi muốn tìm bún chả như ảnh. Hãy dùng công cụ tìm quầy rồi trả lời ngắn.",
                [new("public.food", "Tìm quầy theo tên món", schema)], [], [], false, true, [new(bytes, "image/jpeg")]);
            var selected = await model.GenerateAsync(request, (_, _) => Task.CompletedTask, deadline.Token);
            Assert.NotEmpty(selected.ToolCalls); Assert.NotNull(selected.NativeAssistant);
            Assert.All(selected.ToolCalls, call => Assert.Equal("public.food", call.Name));
            var replies = selected.ToolCalls.Select(call => new ChatbotToolReply(call.Id, call.Name,
                "{\"results\":[],\"note\":\"Dữ liệu giả lập kiểm thử: chưa tìm thấy quầy; không suy ra toàn hệ thống không bán.\"}")).ToArray();
            var result = await model.GenerateAsync(request with { FinalAnswer = true, Tools = [],
                Exchanges = [new("GEMINI", selected.NativeAssistant!.Value, replies)] }, (_, _) => Task.CompletedTask, deadline.Token);
            Assert.False(string.IsNullOrWhiteSpace(result.Text));
            Assert.True(result.Usage.InputTokens > 0);
        }
        finally { Array.Clear(bytes); }
    }
}
