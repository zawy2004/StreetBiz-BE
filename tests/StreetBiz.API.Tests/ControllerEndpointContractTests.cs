using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.DTOs.AdministrativeUnits;

namespace StreetBiz.API.Tests;

public sealed class ControllerEndpointContractTests
{
    private const string TestConnection =
        "Server=unused;Database=unused;Integrated Security=True;TrustServerCertificate=True";

    private static readonly string[] ExpectedControllerOperations =
    [
        "GET /api/administrative-units/wards",
        "GET /api/community/vendors",
        "GET /api/community/vendors/{vendorId}",
        "PUT /api/community/vendors/{vendorId}/comment",
        "POST /api/community/vendors/{vendorId}/reports",
        "POST /api/community/permits/verify",
        "GET /api/platform/me",
        "GET /api/platform/food-categories",
        "POST /api/platform/food-categories",
        "PUT /api/platform/food-categories/{categoryId}",
        "DELETE /api/platform/food-categories/{categoryId}",
        "GET /api/platform/reported-content",
        "GET /api/platform/reported-content/{reportId}",
        "POST /api/platform/reported-content/{reportId}/dismiss",
        "POST /api/platform/reported-content/{reportId}/hide",
        "GET /api/platform/order-complaints",
        "GET /api/platform/order-complaints/{complaintId}",
        "POST /api/platform/order-complaints/{complaintId}/decision",
        "POST /api/auth/send-otp",
        "POST /api/auth/register",
        "POST /api/auth/login",
        "POST /api/auth/refresh",
        "POST /api/auth/logout",
        "POST /api/auth/change-password",
        "POST /api/auth/forgot-password",
        "POST /api/auth/reset-password",
        "GET /api/auth/sessions",
        "DELETE /api/auth/sessions/{sessionId}",
        "POST /api/vendor/address-changes",
        "GET /api/vendor/address-changes",
        "POST /api/vendor/rental-applications/open-slot",
        "POST /api/vendor/rental-applications/adjacent",
        "GET /api/vendor/rental-applications",
        "GET /api/vendor/rental-applications/{applicationId}",
        "POST /api/vendor/rental-applications/{applicationId}/withdraw",
        "GET /api/vendor/rental-contracts",
        "GET /api/vendor/rental-contracts/{contractId}",
        "POST /api/vendor/rental-contracts/{contractId}/renewals",
        "GET /api/vendor/rental-contracts/{contractId}/renewals",
        "POST /api/vendor/rental-contracts/{contractId}/cancel",
        "GET /api/vendor/rental-contracts/{contractId}/permit",
        "GET /api/sidewalk-slots",
        "GET /api/sidewalk-slots/{slotId}",
        "GET /api/sidewalk-slots/{slotId}/quote",
        "GET /api/sidewalk-zones/{zoneId}",
        "POST /api/vendor/slot-holds",
        "GET /api/vendor/slot-holds",
        "DELETE /api/vendor/slot-holds/{slotId}",
        "POST /api/vendor/slot-proposals",
        "GET /api/vendor/slot-proposals",
        "POST /api/vendor/slot-transfers",
        "GET /api/vendor/slot-transfers",
        "POST /api/vendor/slot-transfers/{transferId}/accept",
        "POST /api/vendor/slot-transfers/{transferId}/decline",
        "POST /api/uploads/evidence",
        "GET /api/uploads/evidence/{ownerUserId}/{fileName}",
        "POST /api/vendor/registrations",
        "GET /api/vendor/registrations",
        "GET /api/vendor/registrations/{registrationId}",
        "POST /api/vendor/registrations/{registrationId}/evidence",
        "PUT /api/vendor/registrations/{registrationId}",
        "POST /api/vendor/registrations/{registrationId}/withdraw",
        "GET /api/ward/me",
        "GET /api/ward/cases/{kind}",
        "GET /api/ward/cases/{kind}/{id}",
        "POST /api/ward/cases/{kind}/{id}/decision",
        "PUT /api/ward/cases/proposals/{id}/location",
        "GET /api/ward/geo/search",
        "POST /api/ward/geo/verify",
    ];

    private static readonly (HttpMethod Method, string Path)[] ProtectedEndpoints =
    [
        (HttpMethod.Post, "/api/auth/logout"),
        (HttpMethod.Post, "/api/auth/change-password"),
        (HttpMethod.Put, "/api/community/vendors/1/comment"),
        (HttpMethod.Post, "/api/community/vendors/1/reports"),
        (HttpMethod.Get, "/api/platform/me"),
        (HttpMethod.Get, "/api/platform/food-categories"),
        (HttpMethod.Post, "/api/platform/food-categories"),
        (HttpMethod.Put, "/api/platform/food-categories/1"),
        (HttpMethod.Delete, "/api/platform/food-categories/1"),
        (HttpMethod.Get, "/api/platform/reported-content"),
        (HttpMethod.Get, "/api/platform/reported-content/1"),
        (HttpMethod.Post, "/api/platform/reported-content/1/dismiss"),
        (HttpMethod.Post, "/api/platform/reported-content/1/hide"),
        (HttpMethod.Get, "/api/platform/order-complaints"),
        (HttpMethod.Get, "/api/platform/order-complaints/1"),
        (HttpMethod.Post, "/api/platform/order-complaints/1/decision"),
        (HttpMethod.Get, "/api/auth/sessions"),
        (HttpMethod.Delete, "/api/auth/sessions/1"),
        (HttpMethod.Post, "/api/vendor/address-changes"),
        (HttpMethod.Get, "/api/vendor/address-changes"),
        (HttpMethod.Post, "/api/vendor/rental-applications/open-slot"),
        (HttpMethod.Post, "/api/vendor/rental-applications/adjacent"),
        (HttpMethod.Get, "/api/vendor/rental-applications"),
        (HttpMethod.Get, "/api/vendor/rental-applications/1"),
        (HttpMethod.Post, "/api/vendor/rental-applications/1/withdraw"),
        (HttpMethod.Get, "/api/vendor/rental-contracts"),
        (HttpMethod.Get, "/api/vendor/rental-contracts/1"),
        (HttpMethod.Post, "/api/vendor/rental-contracts/1/renewals"),
        (HttpMethod.Get, "/api/vendor/rental-contracts/1/renewals"),
        (HttpMethod.Post, "/api/vendor/rental-contracts/1/cancel"),
        (HttpMethod.Get, "/api/vendor/rental-contracts/1/permit"),
        (HttpMethod.Get, "/api/sidewalk-slots"),
        (HttpMethod.Get, "/api/sidewalk-slots/1"),
        (HttpMethod.Get, "/api/sidewalk-slots/1/quote?termDays=30"),
        (HttpMethod.Get, "/api/sidewalk-zones/1"),
        (HttpMethod.Post, "/api/vendor/slot-holds"),
        (HttpMethod.Get, "/api/vendor/slot-holds?registrationId=1"),
        (HttpMethod.Delete, "/api/vendor/slot-holds/1?registrationId=1"),
        (HttpMethod.Post, "/api/vendor/slot-proposals"),
        (HttpMethod.Get, "/api/vendor/slot-proposals"),
        (HttpMethod.Post, "/api/vendor/slot-transfers"),
        (HttpMethod.Get, "/api/vendor/slot-transfers?direction=outgoing"),
        (HttpMethod.Post, "/api/vendor/slot-transfers/1/accept"),
        (HttpMethod.Post, "/api/vendor/slot-transfers/1/decline"),
        (HttpMethod.Post, "/api/uploads/evidence"),
        (HttpMethod.Get, "/api/uploads/evidence/1/missing.pdf"),
        (HttpMethod.Post, "/api/vendor/registrations"),
        (HttpMethod.Get, "/api/vendor/registrations"),
        (HttpMethod.Get, "/api/vendor/registrations/1"),
        (HttpMethod.Post, "/api/vendor/registrations/1/evidence"),
        (HttpMethod.Put, "/api/vendor/registrations/1"),
        (HttpMethod.Post, "/api/vendor/registrations/1/withdraw"),
        (HttpMethod.Get, "/api/ward/me"),
        (HttpMethod.Get, "/api/ward/cases/proposals"),
        (HttpMethod.Get, "/api/ward/cases/proposals/1"),
        (HttpMethod.Post, "/api/ward/cases/proposals/1/decision"),
        (HttpMethod.Put, "/api/ward/cases/proposals/1/location"),
        (HttpMethod.Get, "/api/ward/geo/search?address=test"),
        (HttpMethod.Post, "/api/ward/geo/verify"),
    ];

    private static readonly string[] PublicAuthEndpoints =
    [
        "/api/auth/send-otp",
        "/api/auth/register",
        "/api/auth/login",
        "/api/auth/refresh",
        "/api/auth/forgot-password",
        "/api/auth/reset-password",
    ];

    [Fact]
    public async Task Swagger_exposes_every_controller_operation()
    {
        using var host = Host("Development");
        using var client = host.CreateClient(new() { BaseAddress = new("https://localhost") });

        using var document = JsonDocument.Parse(await client.GetStringAsync("/swagger/v1/swagger.json"));
        var actual = document.RootElement.GetProperty("paths")
            .EnumerateObject()
            .Where(path => !path.NameEquals("/api/dev/ward-session"))
            .SelectMany(path => path.Value.EnumerateObject()
                .Where(operation => IsHttpMethod(operation.Name))
                .Select(operation => $"{operation.Name.ToUpperInvariant()} {path.Name}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ExpectedControllerOperations.Order(StringComparer.Ordinal), actual);
    }

    [Fact]
    public async Task Every_protected_controller_operation_rejects_anonymous_requests()
    {
        using var host = Host();
        using var client = host.CreateClient(new() { BaseAddress = new("https://localhost") });

        foreach (var endpoint in ProtectedEndpoints)
        {
            using var request = new HttpRequestMessage(endpoint.Method, endpoint.Path);
            if (endpoint.Path == "/api/uploads/evidence")
            {
                request.Content = new MultipartFormDataContent();
            }
            else if (endpoint.Method == HttpMethod.Post || endpoint.Method == HttpMethod.Put)
            {
                request.Content = JsonContent.Create(new { });
            }

            using var response = await client.SendAsync(request);

            Assert.True(
                response.StatusCode == HttpStatusCode.Unauthorized,
                $"{endpoint.Method} {endpoint.Path} returned {(int)response.StatusCode}, expected 401.");
        }
    }

    [Fact]
    public async Task Every_public_auth_operation_rejects_an_empty_contract()
    {
        using var host = Host();
        using var client = host.CreateClient(new() { BaseAddress = new("https://localhost") });

        foreach (var path in PublicAuthEndpoints)
        {
            using var response = await client.PostAsJsonAsync(path, new { });

            Assert.True(
                response.StatusCode == HttpStatusCode.BadRequest,
                $"POST {path} returned {(int)response.StatusCode}, expected 400.");
        }
    }

    [Fact]
    public async Task Ward_picker_is_public_and_returns_repository_data()
    {
        var units = new Mock<IAdministrativeUnitRepository>();
        units.Setup(repository => repository.ListWardsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new WardDto(16, "Ward 16", "District")]);
        using var host = Host(units: units.Object);
        using var client = host.CreateClient(new() { BaseAddress = new("https://localhost") });

        var response = await client.GetAsync("/api/administrative-units/wards");
        var body = await response.Content.ReadFromJsonAsync<WardDto[]>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Collection(body!, ward => Assert.Equal(16, ward.UnitId));
    }

    private static bool IsHttpMethod(string value) =>
        value is "get" or "post" or "put" or "patch" or "delete";

    private static WebApplicationFactory<Program> Host(
        string environment = "Testing",
        IAdministrativeUnitRepository? units = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.UseSetting("ConnectionStrings:StreetBizDB", TestConnection);
            builder.UseSetting("Jwt:SigningKey", "test-only-signing-key-at-least-32-characters");
            builder.UseSetting("Jwt:Issuer", "StreetBiz");
            builder.UseSetting("Jwt:Audience", "StreetBiz");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:StreetBizDB"] = TestConnection,
                    ["Jwt:SigningKey"] = "test-only-signing-key-at-least-32-characters",
                    ["Jwt:Issuer"] = "StreetBiz",
                    ["Jwt:Audience"] = "StreetBiz",
                }));
            if (units is not null)
            {
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IAdministrativeUnitRepository>();
                    services.AddSingleton(units);
                });
            }
        });
}
