using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.API.Tests;

/// <summary>
/// AUTH-03 … AUTH-09 at the HTTP boundary: the status codes and bodies a browser or the mobile app
/// sees. The repositories are mocks; the password hasher, token issuer, validators, exception
/// handler and JWT bearer authentication are the real ones.
/// </summary>
public sealed class AuthApiTests
{
    private const string Key = "test-only-signing-key-at-least-32-characters";
    private const string Phone = "0905000201";
    private const string Password = "Password123!";
    private const long UserId = 9;
    private const long SessionId = 90;

    private readonly Mock<IUserAccountRepository> users = new();
    private readonly Mock<ISessionRepository> sessions = new();
    private readonly Mock<ISecurityEvents> events = new();

    private WebApplicationFactory<Program> Host() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            // The connection string and the key are read while the services are registered,
            // so they have to be host settings, not only a later configuration source.
            builder.UseSetting("ConnectionStrings:StreetBizDB", "Server=unused;Database=unused;Integrated Security=True");
            builder.UseSetting("Jwt:SigningKey", Key);
            builder.UseSetting("Jwt:Issuer", "StreetBiz");
            builder.UseSetting("Jwt:Audience", "StreetBiz");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:StreetBizDB"] = "Server=unused;Database=unused;Integrated Security=True",
                ["Jwt:SigningKey"] = Key,
                ["Jwt:Issuer"] = "StreetBiz",
                ["Jwt:Audience"] = "StreetBiz",
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IUserAccountRepository>();
                services.AddSingleton(users.Object);
                services.RemoveAll<ISessionRepository>();
                services.AddSingleton(sessions.Object);
                services.RemoveAll<ISecurityEvents>();
                services.AddSingleton(events.Object);
            });
        });

    private static HttpClient Client(WebApplicationFactory<Program> host) =>
        host.CreateClient(new() { BaseAddress = new("https://localhost") });

    private AppUser Account(WebApplicationFactory<Program> host, string status = "ACTIVE", DateTime? lockoutUntil = null)
    {
        var hash = host.Services.GetRequiredService<IPasswordHasher>().Hash(Password);
        return new AppUser(UserId, Phone, hash, "Trần Hồng Anh", RoleCodes.Customer, null, status, DateTime.UtcNow, lockoutUntil);
    }

    private static async Task<string> Error(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.ToString();
    }

    // ---- every protected endpoint refuses an anonymous caller ---------------------------

    [Theory]
    [InlineData("POST", "/api/auth/logout")]
    [InlineData("POST", "/api/auth/change-password")]
    [InlineData("GET", "/api/auth/sessions")]
    [InlineData("DELETE", "/api/auth/sessions")]
    [InlineData("DELETE", "/api/auth/sessions/5")]
    [InlineData("GET", "/api/auth/login-history")]
    public async Task Protected_auth_endpoints_answer_401_without_a_token(string method, string url)
    {
        using var host = Host();
        using var client = Client(host);

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), url)
        {
            Content = method == "POST" ? JsonContent.Create(new { currentPassword = "a", newPassword = "b" }) : null,
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_token_that_is_not_a_jwt_is_refused()
    {
        using var host = Host();
        using var client = Client(host);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-token");

        var response = await client.GetAsync("/api/auth/sessions");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- login --------------------------------------------------------------------------

    [Fact]
    public async Task Login_with_an_empty_body_is_a_400_before_the_database_is_asked()
    {
        using var host = Host();
        using var client = Client(host);

        var response = await client.PostAsJsonAsync("/api/auth/login", new { phoneNumber = "", password = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        users.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task An_unknown_phone_and_a_wrong_password_look_exactly_the_same()
    {
        using var host = Host();
        users.Setup(u => u.GetByPhoneAsync("0900000999", It.IsAny<CancellationToken>())).ReturnsAsync((AppUser?)null);
        users.Setup(u => u.GetByPhoneAsync(Phone, It.IsAny<CancellationToken>())).ReturnsAsync(Account(host));
        using var client = Client(host);

        var unknown = await client.PostAsJsonAsync("/api/auth/login", new { phoneNumber = "0900000999", password = Password });
        var wrong = await client.PostAsJsonAsync("/api/auth/login", new { phoneNumber = Phone, password = "Wrong-password-1" });

        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(await Error(unknown), await Error(wrong));
    }

    [Fact]
    public async Task A_wrong_password_is_counted_and_recorded_but_does_not_open_a_session()
    {
        using var host = Host();
        users.Setup(u => u.GetByPhoneAsync(Phone, It.IsAny<CancellationToken>())).ReturnsAsync(Account(host));
        using var client = Client(host);

        await client.PostAsJsonAsync("/api/auth/login", new { phoneNumber = Phone, password = "Wrong-password-1" });

        users.Verify(u => u.RecordFailedLoginAsync(UserId, 5, TimeSpan.FromMinutes(15), It.IsAny<CancellationToken>()), Times.Once);
        events.Verify(e => e.RecordAsync(UserId, SecurityActions.LoginFailed, It.IsAny<string?>(),
            It.IsAny<(string, string)?>(), It.IsAny<CancellationToken>()), Times.Once);
        sessions.Verify(s => s.CreateAsync(It.IsAny<long>(), It.IsAny<byte[]>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_locked_account_answers_429_and_does_not_even_check_the_password()
    {
        using var host = Host();
        users.Setup(u => u.GetByPhoneAsync(Phone, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Account(host, lockoutUntil: DateTime.UtcNow.AddMinutes(10)));
        using var client = Client(host);

        var response = await client.PostAsJsonAsync("/api/auth/login", new { phoneNumber = Phone, password = Password });

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
        users.Verify(u => u.RecordFailedLoginAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<TimeSpan>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_suspended_account_with_the_right_password_is_refused()
    {
        using var host = Host();
        users.Setup(u => u.GetByPhoneAsync(Phone, It.IsAny<CancellationToken>())).ReturnsAsync(Account(host, status: "SUSPENDED"));
        using var client = Client(host);

        var response = await client.PostAsJsonAsync("/api/auth/login", new { phoneNumber = Phone, password = Password });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        sessions.Verify(s => s.CreateAsync(It.IsAny<long>(), It.IsAny<byte[]>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_correct_login_returns_tokens_stores_only_a_hash_of_the_refresh_token_and_unlocks_the_session_list()
    {
        using var host = Host();
        var user = Account(host);
        users.Setup(u => u.GetByPhoneAsync(Phone, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        users.Setup(u => u.GetByIdAsync(UserId, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        byte[]? storedHash = null;
        string? storedDevice = null;
        sessions.Setup(s => s.CreateAsync(UserId, It.IsAny<byte[]>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Callback<long, byte[], string?, string?, DateTime, CancellationToken>((_, hash, device, _, _, _) =>
            {
                storedHash = hash;
                storedDevice = device;
            })
            .ReturnsAsync(SessionId);
        var now = DateTime.UtcNow;
        sessions.Setup(s => s.GetByIdAsync(SessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AppSession(SessionId, UserId, "StreetBiz-Tests", "127.0.0.1", now, now, now.AddDays(30), null));
        sessions.Setup(s => s.ListActiveByUserAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AppSession(SessionId, UserId, "StreetBiz-Tests", "127.0.0.1", now, now, now.AddDays(30), null)]);
        using var client = Client(host);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("StreetBiz-Tests");

        var login = await client.PostAsJsonAsync("/api/auth/login", new { phoneNumber = Phone, password = Password });

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var body = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var access = body.RootElement.GetProperty("accessToken").GetString();
        var refresh = body.RootElement.GetProperty("refreshToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(access));
        Assert.False(string.IsNullOrWhiteSpace(refresh));
        Assert.NotNull(storedHash);
        Assert.NotEqual(System.Text.Encoding.UTF8.GetBytes(refresh!), storedHash);
        Assert.Equal("StreetBiz-Tests", storedDevice);
        Assert.DoesNotContain("passwordHash", body.RootElement.ToString(), StringComparison.OrdinalIgnoreCase);
        users.Verify(u => u.ClearFailedLoginsAsync(UserId, It.IsAny<CancellationToken>()), Times.Once);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", access);
        var list = await client.GetAsync("/api/auth/sessions");

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var listed = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var only = Assert.Single(listed.RootElement.EnumerateArray());
        Assert.Equal(SessionId, only.GetProperty("sessionId").GetInt64());
        Assert.True(only.GetProperty("isCurrent").GetBoolean());
    }

    [Fact]
    public async Task A_token_of_a_session_that_was_revoked_is_refused_even_though_it_has_not_expired()
    {
        using var host = Host();
        var user = Account(host);
        users.Setup(u => u.GetByPhoneAsync(Phone, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        users.Setup(u => u.GetByIdAsync(UserId, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        sessions.Setup(s => s.CreateAsync(UserId, It.IsAny<byte[]>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync(SessionId);
        var now = DateTime.UtcNow;
        sessions.Setup(s => s.GetByIdAsync(SessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AppSession(SessionId, UserId, null, null, now, now, now.AddDays(30), now));
        using var client = Client(host);
        var login = await client.PostAsJsonAsync("/api/auth/login", new { phoneNumber = Phone, password = Password });
        using var body = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", body.RootElement.GetProperty("accessToken").GetString());

        var response = await client.GetAsync("/api/auth/sessions");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- OTP and reset endpoints validate their input -----------------------------------

    [Theory]
    [InlineData("/api/auth/send-otp")]
    [InlineData("/api/auth/forgot-password")]
    public async Task Asking_for_an_otp_for_a_malformed_phone_is_a_400(string url)
    {
        using var host = Host();
        using var client = Client(host);

        var response = await client.PostAsJsonAsync(url, new { phoneNumber = "12345", purpose = "SIGNUP" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Refreshing_with_a_token_nobody_issued_is_a_401()
    {
        using var host = Host();
        sessions.Setup(s => s.GetActiveByRefreshHashAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AppSession?)null);
        using var client = Client(host);

        var response = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = "made-up-token" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
