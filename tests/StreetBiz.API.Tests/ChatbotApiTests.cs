using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace StreetBiz.API.Tests;

public sealed class ChatbotApiTests
{
    private static WebApplicationFactory<Program> Host() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:StreetBizDB", "Server=unused;Database=unused;Integrated Security=True;TrustServerCertificate=True");
        builder.UseSetting("Jwt:SigningKey", "test-only-signing-key-at-least-32-characters");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> {
            ["ConnectionStrings:StreetBizDB"] = "Server=unused;Database=unused;Integrated Security=True;TrustServerCertificate=True",
            ["Jwt:SigningKey"] = "test-only-signing-key-at-least-32-characters", ["Jwt:Issuer"] = "StreetBiz", ["Jwt:Audience"] = "StreetBiz",
        }));
    });
    [Fact]
    public async Task Guest_response_has_envelope_sources_ai_label_and_no_store()
    {
        using var host = Host(); using var client = host.CreateClient();
        var response = await client.PostAsJsonAsync("/api/chatbot/guest/messages", new { content = "Làm sao đăng ký bán hàng?" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, json.GetProperty("error").ValueKind);
        Assert.True(json.GetProperty("data").GetProperty("isAiGenerated").GetBoolean());
        Assert.NotEmpty(json.GetProperty("data").GetProperty("sources").EnumerateArray());
        Assert.True(json.GetProperty("meta").TryGetProperty("traceId", out _));
    }
    [Theory]
    [InlineData("/api/chatbot/conversations")]
    [InlineData("/api/chatbot/conversations/unknown/messages")]
    public async Task Private_history_requires_authentication_even_when_id_is_known(string path)
    {
        using var host = Host(); using var client = host.CreateClient();
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("session_expired", json.GetProperty("error").GetProperty("code").GetString());
    }
    [Fact]
    public async Task Oversize_and_sensitive_requests_are_rejected_with_envelope()
    {
        using var host = Host(); using var client = host.CreateClient();
        var large = await client.PostAsJsonAsync("/api/chatbot/guest/messages", new { content = new string('a', 17000) });
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, large.StatusCode);
        var sensitive = await client.PostAsJsonAsync("/api/chatbot/guest/messages", new { content = "OTP: 123456" });
        Assert.Equal(HttpStatusCode.BadRequest, sensitive.StatusCode);
        var json = await sensitive.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("sensitive_content", json.GetProperty("error").GetProperty("code").GetString());
    }
    [Fact]
    public async Task Guest_rate_limit_is_429_not_generic_503()
    {
        using var host = Host(); using var client = host.CreateClient();
        HttpResponseMessage? response = null;
        for (var i = 0; i < 4; i++) response = await client.PostAsJsonAsync("/api/chatbot/guest/messages", new { content = "Xin chào" });
        Assert.Equal(HttpStatusCode.TooManyRequests, response!.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
    }
}
