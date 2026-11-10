using System.IdentityModel.Tokens.Jwt;
using System.Net;
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
using StreetBiz.API.Controllers;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.Features.VendorReports;
using StreetBiz.Application.Features.WardSlots;

namespace StreetBiz.API.Tests;

/// <summary>BUY-06/07 and WARD-19 at the HTTP boundary: caching, rate limits, the request contract and errors.</summary>
public sealed class PriceQrComplaintApiTests
{
    private const string Key = "test-only-signing-key-at-least-32-characters";
    private const long CustomerId = 7;
    private const long SessionId = 70;
    private const long WardOfficerId = 42;

    [Fact]
    public async Task A_public_permit_check_is_never_cacheable()
    {
        var community = new Mock<ICommunityVendorRepository>();
        using var host = Host(community: community.Object);
        using var client = host.CreateClient(new() { BaseAddress = new("https://localhost") });

        var response = await client.PostAsJsonAsync("/api/community/permits/verify",
            new { qrPayload = "https://streetbiz.vn/q/SBP1.forged.token?v=2" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore, "BR-39: the verdict and the price board must not be stored.");
        community.Verify(x => x.AddPermitScanAsync(
            It.Is<PublicPermitScan>(scan => scan.Result == PermitScanResults.NotFound), It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task The_posted_price_of_a_complaint_comes_from_the_server_whatever_the_client_sends()
    {
        Assert.DoesNotContain(typeof(ReportSuspiciousVendorRequest).GetProperties(),
            property => property.Name.Contains("Posted", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("UnitPrice", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("PriceHistory", StringComparison.OrdinalIgnoreCase));

        var community = ReportableVendor();
        var purchasedAt = DateTimeOffset.UtcNow.AddHours(-2);
        community.Setup(x => x.FindPostedPriceAsync(2, 12, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PostedPriceRow(12, 1, "Bún chả Cô Lan", "A-01", 77, "Bún chả", "suất", 45_000, true, DateTime.UtcNow.AddDays(-9)));
        CustomerVendorReport? saved = null;
        community.Setup(x => x.AddReportAsync(2, CustomerId, It.IsAny<CustomerVendorReport>(), It.IsAny<NewVendorReportNotices>(), It.IsAny<CancellationToken>()))
            .Callback<long, long, CustomerVendorReport, NewVendorReportNotices, CancellationToken>((_, _, report, _, _) => saved = report)
            .ReturnsAsync(41);
        using var host = Host(community: community.Object);
        using var client = CustomerClient(host);

        var response = await client.PostAsJsonAsync("/api/community/vendors/2/reports", new
        {
            reportType = "OVERCHARGE",
            menuItemId = 12,
            quantity = 2,
            chargedAmount = 120_000,
            purchasedAt,
            postedAmount = 1,
            unitPrice = 1,
            priceHistoryId = 1,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(90_000m, saved!.PostedAmount);
        Assert.Equal(77, saved.PriceHistoryId);
    }

    [Fact]
    public async Task A_customer_may_file_five_reports_an_hour_and_no_more()
    {
        var community = new Mock<ICommunityVendorRepository>();
        using var host = Host(community: community.Object);
        using var client = CustomerClient(host);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
        {
            statuses.Add((await client.PostAsJsonAsync("/api/community/vendors/2/reports", new { reason = "Lấn vỉa hè" })).StatusCode);
        }

        // The first five reach the handler (the vendor is unknown here, so 404); the sixth is refused before it.
        Assert.All(statuses.Take(5), status => Assert.Equal(HttpStatusCode.NotFound, status));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[5]);
    }

    [Fact]
    public async Task A_report_that_moved_on_answers_409_problem_details_and_a_missing_note_400()
    {
        var decisions = new Mock<IVendorReportDecisions>();
        decisions.Setup(x => x.ResolveAsync(It.IsAny<WardActor>(), 1, It.IsAny<ResolveVendorReportRequest>(), It.IsAny<CancellationToken>()))
            .Returns(() => { WardRules.Conflict(CommunityMessages.ReportAlreadyClosed); return Task.CompletedTask; });
        using var host = Host(decisions: decisions.Object);
        using var client = host.CreateClient(new() { BaseAddress = new("https://localhost") });
        client.DefaultRequestHeaders.Authorization = new("Bearer", WardToken());

        var conflict = await client.PostAsJsonAsync("/api/ward/vendor-reports/1/resolve",
            new ResolveVendorReportRequest(VendorReportStatuses.Pending, VendorReportResolutions.Dismissed, "Không đủ căn cứ"));
        var noNote = await client.PostAsJsonAsync("/api/ward/vendor-reports/1/resolve",
            new ResolveVendorReportRequest(VendorReportStatuses.Pending, VendorReportResolutions.Dismissed, " "));

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var problem = await conflict.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        Assert.Equal(("review_conflict", 409, CommunityMessages.ReportAlreadyClosed), (problem!.Title, problem.Status, problem.Detail));
        Assert.Equal(HttpStatusCode.BadRequest, noNote.StatusCode);
        decisions.Verify(x => x.ResolveAsync(It.IsAny<WardActor>(), 1, It.IsAny<ResolveVendorReportRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static Mock<ICommunityVendorRepository> ReportableVendor()
    {
        var community = new Mock<ICommunityVendorRepository>();
        community.Setup(x => x.GetReportTargetAsync(2, It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReportTargetRow(2, "Bún chả Cô Lan", "A-01"));
        community.Setup(x => x.ReportReferencesBelongToVendorAsync(2, It.IsAny<long?>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return community;
    }

    private static HttpClient CustomerClient(WebApplicationFactory<Program> host)
    {
        var client = host.CreateClient(new() { BaseAddress = new("https://localhost") });
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token(
            [new Claim("sub", CustomerId.ToString()), new Claim("sid", SessionId.ToString()), new Claim(ClaimTypes.Role, RoleCodes.Customer)]));
        return client;
    }

    private static string WardToken() => Token(
        [new Claim("sub", WardOfficerId.ToString()), new Claim("ward_dev", "true"), new Claim("role", RoleCodes.WardAuthority)]);

    private static string Token(IEnumerable<Claim> claims) =>
        new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("StreetBiz", "StreetBiz", claims,
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(10),
            new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key)), SecurityAlgorithms.HmacSha256)));

    private static WebApplicationFactory<Program> Host(
        ICommunityVendorRepository? community = null,
        IVendorReportDecisions? decisions = null) =>
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
                services.AddSingleton(community ?? Mock.Of<ICommunityVendorRepository>());
                services.RemoveAll<IVendorReportDecisions>();
                services.AddSingleton(decisions ?? Mock.Of<IVendorReportDecisions>());

                // A signed-in customer: an open session and an active CUSTOMER account.
                var sessions = new Mock<ISessionRepository>();
                sessions.Setup(x => x.GetByIdAsync(SessionId, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new AppSession(SessionId, CustomerId, null, null, DateTime.UtcNow, DateTime.UtcNow,
                        DateTime.UtcNow.AddDays(1), null));
                services.RemoveAll<ISessionRepository>();
                services.AddSingleton(sessions.Object);
                var users = new Mock<IUserAccountRepository>();
                users.Setup(x => x.GetByIdAsync(CustomerId, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new AppUser(CustomerId, "0905000201", "x", "Khách", RoleCodes.Customer, null,
                        AccountStatuses.Active, DateTime.UtcNow));
                services.RemoveAll<IUserAccountRepository>();
                services.AddSingleton(users.Object);

                var actors = new Mock<IWardActorResolver>();
                actors.Setup(x => x.ResolveAsync(WardOfficerId, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new WardActor(WardOfficerId, 7, "Cán bộ"));
                services.RemoveAll<IWardActorResolver>();
                services.AddSingleton(actors.Object);
            });
        });
}
