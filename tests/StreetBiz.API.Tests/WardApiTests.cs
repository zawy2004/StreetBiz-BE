using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Moq;
using StreetBiz.Application.Features.WardSlots;

namespace StreetBiz.API.Tests;

public sealed class WardApiTests
{
    private const string Key = "test-only-signing-key-at-least-32-characters";
    private const string DevKey = "test-only-development-key-at-least-32-characters";

    [Fact]
    public async Task Anonymous_cannot_read_or_decide()
    {
        using var host = Host();
        using var client = host.CreateClient(new() { BaseAddress = new("https://localhost") });
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/ward/cases/proposals")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/ward/cases/proposals/1/decision",
            new ReviewDecision("APPROVE", "X", "PENDING"))).StatusCode);
    }

    [Theory]
    [InlineData("wrong-issuer", false, true)]
    [InlineData("StreetBiz", true, true)]
    [InlineData("StreetBiz", false, false)]
    public async Task Invalid_or_inactive_token_is_denied(string issuer, bool expired, bool active)
    {
        using var host = Host(active: active);
        using var client = host.CreateClient(new() { BaseAddress = new("https://localhost") });
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token(issuer, expired));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/ward/me")).StatusCode);
    }

    [Fact]
    public async Task Authenticated_request_uses_database_ward_not_caller_input()
    {
        using var host = Host();
        using var client = host.CreateClient(new() { BaseAddress = new("https://localhost") });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token());
        var response = await client.GetAsync("/api/ward/me?wardId=999");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var profile = await response.Content.ReadFromJsonAsync<Profile>();
        Assert.Equal(7, profile!.WardId);
    }

    [Fact]
    public async Task Development_key_cannot_select_arbitrary_user()
    {
        using var host = Host();
        using var client = host.CreateClient(new() { BaseAddress = new("https://localhost") });
        var denied = await client.PostAsJsonAsync("/api/dev/ward-session", new { accessKey = "wrong" });
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        var response = await client.PostAsJsonAsync("/api/dev/ward-session", new { accessKey = DevKey, userId = 999 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TokenBody>();
        client.DefaultRequestHeaders.Authorization = new("Bearer", body!.AccessToken);
        var me = await client.GetFromJsonAsync<Profile>("/api/ward/me");
        Assert.Equal("42", me!.UserId);
    }

    [Fact]
    public async Task Development_session_does_not_exist_in_production()
    {
        using var host = Host("Production");
        using var client = host.CreateClient(new() { BaseAddress = new("https://localhost") });
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync("/api/dev/ward-session", new { accessKey = DevKey })).StatusCode);
    }

    [Fact]
    public async Task Business_error_returns_problem_details()
    {
        var service = new Mock<IWardSlots>();
        service.Setup(x => x.GetAsync(It.IsAny<WardActor>(), "proposals", 1, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new WardException(404, "not_found", "Không tìm thấy hồ sơ."));
        using var host = Host(slots: service.Object);
        using var client = host.CreateClient(new() { BaseAddress = new("https://localhost") });
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token());
        var response = await client.GetAsync("/api/ward/cases/proposals/1");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("not_found", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Invalid_case_kind_is_rejected_before_the_transaction_service()
    {
        var service = new Mock<IWardSlots>(MockBehavior.Strict);
        using var host = Host(slots: service.Object);
        using var client = host.CreateClient(new() { BaseAddress = new("https://localhost") });
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token());

        var response = await client.GetAsync("/api/ward/cases/unknown");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        service.VerifyNoOtherCalls();
    }

    private static WebApplicationFactory<Program> Host(string environment = "Testing", bool active = true, IWardSlots? slots = null)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.UseSetting("ConnectionStrings:StreetBizDatabase", "Server=unused;Database=unused;Integrated Security=True");
            builder.UseSetting("Jwt:SigningKey", Key);
            builder.UseSetting("Jwt:Issuer", "StreetBiz");
            builder.UseSetting("Jwt:Audience", "StreetBiz");
            builder.UseSetting("WardDevelopment:AccessKey", DevKey);
            builder.UseSetting("WardDevelopment:UserId", "42");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:StreetBizDatabase"] = "Server=unused;Database=unused;Integrated Security=True",
                ["Jwt:SigningKey"] = Key,
                ["Jwt:Issuer"] = "StreetBiz",
                ["Jwt:Audience"] = "StreetBiz",
                ["WardDevelopment:AccessKey"] = DevKey,
                ["WardDevelopment:UserId"] = "42"
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IWardActorResolver>();
                var actors = new Mock<IWardActorResolver>();
                actors.Setup(x => x.ResolveAsync(42, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(active ? new WardActor(42, 7, "Test Ward") : null);
                services.AddSingleton(actors.Object);
                services.RemoveAll<IWardSlots>();
                services.AddSingleton(slots ?? Mock.Of<IWardSlots>());
            });
        });
    }
    private static string Token(string issuer = "StreetBiz", bool expired = false) =>
        new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(issuer, "StreetBiz",
            [new Claim("sub", "42"), new Claim("wardId", "999"), new Claim("ward_dev", "true"),
                new Claim("role", "WARD_AUTHORITY")],
            DateTime.UtcNow.AddHours(-2), expired ? DateTime.UtcNow.AddHours(-1) : DateTime.UtcNow.AddMinutes(10),
            new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key)), SecurityAlgorithms.HmacSha256)));
    private sealed record Profile(string UserId, int WardId);
    private sealed record TokenBody(string AccessToken);
}
