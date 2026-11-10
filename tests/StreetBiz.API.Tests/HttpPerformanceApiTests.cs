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
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.API.Tests;

/// <summary>
/// Output caching and response compression at the HTTP boundary: public reads are served from the
/// cache (also for a signed-in caller), a different query is a different entry, a successful write
/// evicts what it changed, and sign-in responses are never compressed.
/// </summary>
public sealed class HttpPerformanceApiTests
{
    private const string Key = "test-only-signing-key-at-least-32-characters";
    private const long CustomerId = 7;
    private const long SessionId = 70;

    [Fact]
    public async Task The_vendor_map_is_read_once_and_then_served_from_the_cache()
    {
        var community = Community();
        await using var host = Host(community.Object);
        using var client = host.CreateClient(new() { BaseAddress = new("https://localhost") });

        var first = await client.GetAsync("/api/community/vendors?take=20");
        var second = await client.GetAsync("/api/community/vendors?take=20");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
        community.Verify(x => x.SearchActiveAsync(It.IsAny<CommunityVendorSearchArea>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_signed_in_customer_shares_the_public_cache_entry()
    {
        var community = Community();
        await using var host = Host(community.Object);
        using var anonymous = host.CreateClient(new() { BaseAddress = new("https://localhost") });
        using var customer = CustomerClient(host);

        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/api/community/vendors?take=20")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await customer.GetAsync("/api/community/vendors?take=20")).StatusCode);

        community.Verify(x => x.SearchActiveAsync(It.IsAny<CommunityVendorSearchArea>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_different_query_is_a_different_cache_entry()
    {
        var community = Community();
        await using var host = Host(community.Object);
        using var client = host.CreateClient(new() { BaseAddress = new("https://localhost") });

        await client.GetAsync("/api/community/vendors?take=20");
        await client.GetAsync("/api/community/vendors?take=30");

        community.Verify(x => x.SearchActiveAsync(It.IsAny<CommunityVendorSearchArea>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Each_allowed_origin_gets_its_own_cors_header_from_the_cache()
    {
        await using var host = Host(Community().Object);
        using var client = host.CreateClient(new() { BaseAddress = new("https://localhost") });

        string? AllowedOrigin(HttpResponseMessage r) =>
            r.Headers.TryGetValues("Access-Control-Allow-Origin", out var v) ? v.Single() : null;
        async Task<HttpResponseMessage> From(string origin)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/community/vendors?take=20");
            request.Headers.Add("Origin", origin);
            return await client.SendAsync(request);
        }

        var first = await From("http://localhost:5173");
        var second = await From("http://localhost:4173");
        var third = await From("http://localhost:5173");

        Assert.Equal("http://localhost:5173", AllowedOrigin(first));
        Assert.Equal("http://localhost:4173", AllowedOrigin(second));
        Assert.Equal("http://localhost:5173", AllowedOrigin(third));
    }

    [Fact]
    public async Task An_error_response_is_not_cached()
    {
        var community = Community();
        community.Setup(x => x.GetPublicProfileAsync(404, It.IsAny<CancellationToken>()))
            .ReturnsAsync((PublicVendorProfileRow?)null);
        await using var host = Host(community.Object);
        using var client = host.CreateClient(new() { BaseAddress = new("https://localhost") });

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/community/vendors/404")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/community/vendors/404")).StatusCode);

        community.Verify(x => x.GetPublicProfileAsync(404, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Posting_a_rating_evicts_the_cached_vendor_map_so_the_next_read_is_fresh()
    {
        var community = Community();
        await using var host = Host(community.Object);
        using var client = CustomerClient(host);

        await client.GetAsync("/api/community/vendors?take=20");
        var put = await client.PutAsJsonAsync("/api/community/vendors/2/comment", new { rating = 5, commentText = "Ngon" });
        await client.GetAsync("/api/community/vendors?take=20");

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        community.Verify(x => x.SearchActiveAsync(It.IsAny<CommunityVendorSearchArea>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task A_rejected_rating_leaves_the_cache_alone()
    {
        var community = Community();
        await using var host = Host(community.Object);
        using var client = CustomerClient(host);

        await client.GetAsync("/api/community/vendors?take=20");
        var put = await client.PutAsJsonAsync("/api/community/vendors/2/comment", new { rating = 9, commentText = "" });
        await client.GetAsync("/api/community/vendors?take=20");

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        community.Verify(x => x.SearchActiveAsync(It.IsAny<CommunityVendorSearchArea>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("br")]
    [InlineData("gzip")]
    public async Task Public_json_is_compressed_when_the_client_accepts_it(string encoding)
    {
        await using var host = Host(Community().Object);
        using var client = host.CreateClient(new() { BaseAddress = new("https://localhost") });
        client.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue(encoding));

        var response = await client.GetAsync("/api/community/vendors?take=20");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(encoding, response.Content.Headers.ContentEncoding);
    }

    [Fact]
    public async Task Without_accept_encoding_the_body_is_plain()
    {
        await using var host = Host(Community().Object);
        using var client = host.CreateClient(new() { BaseAddress = new("https://localhost") });

        var response = await client.GetAsync("/api/community/vendors?take=20");

        Assert.Empty(response.Content.Headers.ContentEncoding);
        Assert.Contains("Quán", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Sign_in_responses_are_never_compressed()
    {
        await using var host = Host(Community().Object);
        using var client = CustomerClient(host);
        client.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));
        client.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));

        var sessions = await client.GetAsync("/api/auth/sessions");
        var login = await client.PostAsJsonAsync("/api/auth/login", new { });

        Assert.Equal(HttpStatusCode.OK, sessions.StatusCode);
        Assert.Empty(sessions.Content.Headers.ContentEncoding);
        Assert.Empty(login.Content.Headers.ContentEncoding);
    }

    private static Mock<ICommunityVendorRepository> Community()
    {
        var community = new Mock<ICommunityVendorRepository>();
        community.Setup(x => x.SearchActiveAsync(It.IsAny<CommunityVendorSearchArea>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new ActiveVendorLocationRow(2, 20, "Quán Bánh Mì Cô Ba", "FOOD", "12 Lê Lợi", 30, new DateOnly(2027, 1, 1),
                    40, "A-01", "Khu A", 10.776m, 106.700m, 4.5m, 3, null, 0),
            ]);
        community.Setup(x => x.GetPublicProfileAsync(2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PublicVendorProfileRow(2, 20, "Quán Bánh Mì Cô Ba", "FOOD", "12 Lê Lợi", 7, "Bến Thành", 30,
                "ACTIVE", new DateOnly(2027, 1, 1), 40, "A-01", "Khu A", 10.776m, 106.700m, 4.5m, 3, null, 0));
        community.Setup(x => x.UpsertCommentAsync(2, CustomerId, It.IsAny<CustomerVendorComment>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PublicVendorCommentRow(1, CustomerId, "Khách", 5, "Ngon", DateTime.UtcNow));
        return community;
    }

    private static HttpClient CustomerClient(WebApplicationFactory<Program> host)
    {
        var client = host.CreateClient(new() { BaseAddress = new("https://localhost") });
        client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(
            new JwtSecurityToken("StreetBiz", "StreetBiz",
                [new Claim("sub", CustomerId.ToString()), new Claim("sid", SessionId.ToString()), new Claim(ClaimTypes.Role, RoleCodes.Customer)],
                DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(10),
                new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key)), SecurityAlgorithms.HmacSha256))));
        return client;
    }

    private static WebApplicationFactory<Program> Host(ICommunityVendorRepository community) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
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
                services.RemoveAll<ICommunityVendorRepository>();
                services.AddSingleton(community);

                var sessions = new Mock<ISessionRepository>();
                var session = new AppSession(SessionId, CustomerId, null, null, DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow.AddDays(1), null);
                sessions.Setup(x => x.GetByIdAsync(SessionId, It.IsAny<CancellationToken>())).ReturnsAsync(session);
                sessions.Setup(x => x.ListActiveByUserAsync(CustomerId, It.IsAny<CancellationToken>())).ReturnsAsync([session]);
                services.RemoveAll<ISessionRepository>();
                services.AddSingleton(sessions.Object);

                var users = new Mock<IUserAccountRepository>();
                users.Setup(x => x.GetByIdAsync(CustomerId, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new AppUser(CustomerId, "0905000201", "x", "Khách", RoleCodes.Customer, null, AccountStatuses.Active, DateTime.UtcNow));
                services.RemoveAll<IUserAccountRepository>();
                services.AddSingleton(users.Object);
            });
        });
}
