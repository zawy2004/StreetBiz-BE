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
        "GET /api/seller/storefronts",
        "POST /api/seller/storefronts",
        "PUT /api/seller/storefronts/{storefrontId}",
        "GET /api/seller/storefronts/food-categories",
        "GET /api/seller/storefronts/{storefrontId}/menu-items",
        "POST /api/seller/storefronts/{storefrontId}/menu-items",
        "PUT /api/seller/storefronts/{storefrontId}/menu-items/{itemId}",
        "DELETE /api/seller/storefronts/{storefrontId}/menu-items/{itemId}",
        "GET /api/orders/{orderId}/complaints",
        "POST /api/orders/{orderId}/complaints",
        "GET /api/orders/{orderId}/review",
        "PUT /api/orders/{orderId}/review",
        "GET /api/orders/payment-options",
        "POST /api/orders/{orderId}/payment/sandbox-fail",
        "POST /api/orders/{orderId}/payment/sync",
        "POST /api/orders/{orderId}/refund/sandbox-confirm",
        "GET /api/administrative-units/wards",
        "GET /api/community/vendors",
        "GET /api/community/vendors/{vendorId}",
        "PUT /api/community/vendors/{vendorId}/comment",
        "POST /api/community/vendors/{vendorId}/reports",
        "POST /api/community/permits/verify",
        "GET /api/marketplace/menu-items",
        "GET /api/marketplace/menu-items/{menuItemId}",
        "GET /api/marketplace/service-areas",
        "GET /api/marketplace/categories",
        "GET /api/marketplace/storefronts",
        "GET /api/marketplace/storefronts/{storefrontId}",
        "GET /api/cart",
        "POST /api/cart/items",
        "PUT /api/cart/items/{menuItemId}",
        "DELETE /api/cart/items/{menuItemId}",
        "DELETE /api/cart",
        "GET /api/orders",
        "GET /api/orders/me",
        "POST /api/orders",
        "POST /api/orders/checkout",
        "GET /api/orders/{orderId}",
        "GET /api/orders/{orderId}/pickup-code",
        "POST /api/orders/{orderId}/cancel",
        "GET /api/seller/orders",
        "GET /api/seller/orders/{orderId}",
        "POST /api/seller/orders/{orderId}/decision",
        "POST /api/seller/orders/{orderId}/status",
        "GET /api/seller/orders/sales-summary",
        "POST /api/payments/{provider}/callback",
        "GET /api/chat/conversations",
        "POST /api/chat/conversations",
        "GET /api/chat/conversations/{conversationId}",
        "GET /api/chat/conversations/{conversationId}/messages",
        "POST /api/chat/conversations/{conversationId}/messages",
        "GET /api/chat/unread-count",
        "GET /api/notifications",
        "GET /api/notifications/unread-count",
        "POST /api/notifications/{notificationId}/read",
        "POST /api/notifications/read-all",
        "GET /api/vendor/orders",
        "GET /api/vendor/orders/{orderId}",
        "POST /api/vendor/orders/{orderId}/accept",
        "POST /api/vendor/orders/{orderId}/reject",
        "POST /api/vendor/orders/{orderId}/preparing",
        "POST /api/vendor/orders/{orderId}/ready-for-pickup",
        "POST /api/vendor/orders/pickup-scan",
        "POST /api/vendor/orders/pickup-confirm",
        "POST /api/vendor/orders/{orderId}/handover-without-code",
        "GET /api/vendor/orders/sales-summary",
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
        "POST /api/auth/login-otp",
        "POST /api/auth/refresh",
        "POST /api/auth/logout",
        "POST /api/auth/change-password",
        "POST /api/auth/forgot-password",
        "POST /api/auth/reset-password",
        "GET /api/auth/sessions",
        "DELETE /api/auth/sessions/{sessionId}",
        "DELETE /api/auth/sessions",
        "GET /api/auth/login-history",
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
        "POST /api/vendor/rental-contracts/{contractId}/renewals/{renewalId}/withdraw",
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
        "POST /api/uploads/menu-images",
        "GET /api/uploads/menu-images/{ownerUserId}/{fileName}",
        "GET /api/vendor/food-safety",
        "POST /api/vendor/food-safety",
        "GET /api/vendor/food-safety/{id}",
        "PUT /api/vendor/food-safety/{id}",
        "POST /api/vendor/food-safety/{id}/withdraw",
        "GET /api/ward/food-safety",
        "GET /api/ward/food-safety/{id}",
        "POST /api/ward/food-safety/{id}/decision",
        "POST /api/vendor/registrations",
        "GET /api/vendor/registrations",
        "GET /api/vendor/registrations/{registrationId}",
        "GET /api/vendor/registrations/{registrationId}/document",
        "POST /api/vendor/registrations/{registrationId}/evidence",
        "PUT /api/vendor/registrations/{registrationId}",
        "POST /api/vendor/registrations/{registrationId}/submit",
        "POST /api/vendor/registrations/{registrationId}/withdraw",
        "DELETE /api/vendor/registrations/{registrationId}/evidence/{evidenceId}",
        "POST /api/vendor/kyc/id-card",
        "POST /api/vendor/kyc/face-match",
        "GET /api/ward/me",
        "GET /api/ward/cases/{kind}",
        "GET /api/ward/cases/{kind}/{id}",
        "GET /api/ward/compliance-policy",
        "PUT /api/ward/compliance-policy",
        "POST /api/ward/cases/{kind}/{id}/decision",
        "PUT /api/ward/cases/proposals/{id}/location",
        "GET /api/ward/geo/search",
        "POST /api/ward/geo/verify",
        "GET /api/ward/enrollments",
        "GET /api/ward/enrollments/{id}",
        "GET /api/ward/enrollments/{id}/document",
        "POST /api/ward/enrollments/{id}/decision",
        "POST /api/ward/enrollments/{id}/confirm-identity",
        "POST /api/ward/enrollments/{id}/claim",
        "GET /api/ward/enrollments/{id}/fast-track-check",
        "GET /api/ward/rental-applications",
        "GET /api/ward/rental-applications/{id}",
        "POST /api/ward/rental-applications/{id}/decision",
        "GET /api/ward/renewals",
        "GET /api/ward/renewals/{id}",
        "POST /api/ward/renewals/{id}/decision",
        "POST /api/ward/renewals/batch-decision",
        "POST /api/ward/permits/inspect",
        "POST /api/ward/permits/{id}/action",
        "GET /api/ward/penalty-schedules",
        "GET /api/ward/violations",
        "GET /api/ward/violations/{id}",
        "GET /api/ward/violations/{id}/document",
        "POST /api/ward/violations",
        "POST /api/ward/violations/{id}/explanation",
        "POST /api/ward/violations/{id}/deliver",
        "POST /api/ward/violations/{id}/sanction",
        "POST /api/ward/ai/document-extract",
        "POST /api/ward/ai/encroachment-check",
        "POST /api/vendor/assistant",
        "GET /api/ward/insights/risk-queue",
        "GET /api/ward/insights/patrol-heatmap",
        "GET /api/ward/reports/collection",
        "GET /api/ward/dashboard",
        "GET /api/vendor/finance/summary",
        "GET /api/vendor/finance/fees",
        "GET /api/vendor/finance/penalties",
        "GET /api/vendor/finance/payments",
        "GET /api/vendor/finance/violations",
        "POST /api/vendor/finance/fees/{feeItemId}/checkout",
        "POST /api/vendor/finance/penalties/{penaltyId}/checkout",
        "POST /api/vendor/finance/payments/{transactionId}/sandbox-confirm",
        "POST /api/vendor/finance/payments/{transactionId}/sync",
        "GET /api/vendor/finance/invoices",
        "GET /api/vendor/finance/invoices/{invoiceId}",
        "GET /api/ward/penalty-schedules/overview",
        "GET /api/ward/penalty-schedules/history",
        "POST /api/ward/penalty-schedules",
        "DELETE /api/ward/penalty-schedules/{id}",
        "GET /api/ward/pricing-zones",
        "GET /api/ward/pricing-zones/{id}",
        "POST /api/ward/pricing-zones",
        "PUT /api/ward/pricing-zones/{id}",
        "DELETE /api/ward/pricing-zones/{id}",
        "POST /api/ward/pricing-zones/{id}/impact-preview",
        "GET /api/ward/pricing-zones/{id}/history",
        "GET /api/ward/slot-grid",
        "POST /api/ward/slot-grid/check",
        "POST /api/ward/slot-grid",
        "PUT /api/ward/slot-grid/{id}",
        "PUT /api/ward/slot-grid/{id}/status",
        "DELETE /api/ward/slot-grid/{id}",
        "POST /api/ward/slot-grid/batch-preview",
        "POST /api/ward/slot-grid/batch",
        "GET /api/ward/slot-grid/{id}/history",
        "POST /api/ward/street-features",
        "PUT /api/ward/street-features/{id}",
        "DELETE /api/ward/street-features/{id}",
    ];

    private static readonly (HttpMethod Method, string Path)[] ProtectedEndpoints =
    [
        (HttpMethod.Post, "/hubs/orders/negotiate?negotiateVersion=1"),
        (HttpMethod.Get, "/api/notifications"),
        (HttpMethod.Get, "/api/notifications/unread-count"),
        (HttpMethod.Post, "/api/notifications/1/read"),
        (HttpMethod.Post, "/api/notifications/read-all"),
        (HttpMethod.Get, "/api/seller/storefronts"),
        (HttpMethod.Post, "/api/seller/storefronts"),
        (HttpMethod.Put, "/api/seller/storefronts/1"),
        (HttpMethod.Get, "/api/seller/storefronts/food-categories"),
        (HttpMethod.Get, "/api/seller/storefronts/1/menu-items"),
        (HttpMethod.Post, "/api/seller/storefronts/1/menu-items"),
        (HttpMethod.Put, "/api/seller/storefronts/1/menu-items/1"),
        (HttpMethod.Delete, "/api/seller/storefronts/1/menu-items/1"),
        (HttpMethod.Get, "/api/orders/1/complaints"),
        (HttpMethod.Post, "/api/orders/1/complaints"),
        (HttpMethod.Get, "/api/orders/1/review"),
        (HttpMethod.Put, "/api/orders/1/review"),
        (HttpMethod.Get, "/api/orders/payment-options"),
        (HttpMethod.Post, "/api/orders/1/payment/sandbox-fail"),
        (HttpMethod.Post, "/api/orders/1/refund/sandbox-confirm"),
        (HttpMethod.Post, "/api/auth/logout"),
        (HttpMethod.Post, "/api/auth/change-password"),
        (HttpMethod.Put, "/api/community/vendors/1/comment"),
        (HttpMethod.Post, "/api/community/vendors/1/reports"),
        (HttpMethod.Get, "/api/cart"),
        (HttpMethod.Post, "/api/cart/items"),
        (HttpMethod.Put, "/api/cart/items/1"),
        (HttpMethod.Delete, "/api/cart/items/1"),
        (HttpMethod.Delete, "/api/cart"),
        (HttpMethod.Get, "/api/orders"),
        (HttpMethod.Get, "/api/orders/me"),
        (HttpMethod.Post, "/api/orders"),
        (HttpMethod.Post, "/api/orders/checkout"),
        (HttpMethod.Get, "/api/orders/1"),
        (HttpMethod.Post, "/api/orders/1/cancel"),
        (HttpMethod.Post, "/api/orders/1/payment/sandbox-confirm"),
        (HttpMethod.Post, "/api/orders/1/payment/sync"),
        (HttpMethod.Get, "/api/seller/orders"),
        (HttpMethod.Get, "/api/seller/orders/1"),
        (HttpMethod.Post, "/api/seller/orders/1/decision"),
        (HttpMethod.Post, "/api/seller/orders/1/status"),
        (HttpMethod.Get, "/api/seller/orders/sales-summary"),
        (HttpMethod.Get, "/api/vendor/orders"),
        (HttpMethod.Get, "/api/vendor/orders/1"),
        (HttpMethod.Post, "/api/vendor/orders/1/accept"),
        (HttpMethod.Post, "/api/vendor/orders/1/reject"),
        (HttpMethod.Post, "/api/vendor/orders/1/preparing"),
        (HttpMethod.Post, "/api/vendor/orders/1/ready-for-pickup"),
        (HttpMethod.Post, "/api/vendor/orders/pickup-scan"),
        (HttpMethod.Post, "/api/vendor/orders/pickup-confirm"),
        (HttpMethod.Post, "/api/vendor/orders/1/handover-without-code"),
        (HttpMethod.Get, "/api/vendor/orders/sales-summary"),
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
        (HttpMethod.Post, "/api/vendor/rental-contracts/1/renewals/1/withdraw"),
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
        (HttpMethod.Post, "/api/uploads/menu-images"),
        (HttpMethod.Get, "/api/vendor/food-safety"),
        (HttpMethod.Post, "/api/vendor/food-safety"),
        (HttpMethod.Get, "/api/vendor/food-safety/1"),
        (HttpMethod.Put, "/api/vendor/food-safety/1"),
        (HttpMethod.Post, "/api/vendor/food-safety/1/withdraw"),
        (HttpMethod.Get, "/api/ward/food-safety"),
        (HttpMethod.Get, "/api/ward/food-safety/1"),
        (HttpMethod.Post, "/api/ward/food-safety/1/decision"),
        (HttpMethod.Post, "/api/vendor/registrations"),
        (HttpMethod.Get, "/api/vendor/registrations"),
        (HttpMethod.Get, "/api/vendor/registrations/1"),
        (HttpMethod.Get, "/api/vendor/registrations/1/document"),
        (HttpMethod.Post, "/api/vendor/registrations/1/evidence"),
        (HttpMethod.Put, "/api/vendor/registrations/1"),
        (HttpMethod.Post, "/api/vendor/registrations/1/withdraw"),
        (HttpMethod.Get, "/api/ward/me"),
        (HttpMethod.Get, "/api/ward/cases/proposals"),
        (HttpMethod.Get, "/api/ward/cases/proposals/1"),
        (HttpMethod.Post, "/api/ward/cases/proposals/1/decision"),
        (HttpMethod.Put, "/api/ward/cases/proposals/1/location"),
        (HttpMethod.Get, "/api/ward/compliance-policy"),
        (HttpMethod.Put, "/api/ward/compliance-policy"),
        (HttpMethod.Get, "/api/ward/geo/search?address=test"),
        (HttpMethod.Post, "/api/ward/geo/verify"),
        (HttpMethod.Get, "/api/ward/enrollments"),
        (HttpMethod.Get, "/api/ward/enrollments/1"),
        (HttpMethod.Get, "/api/ward/enrollments/1/document"),
        (HttpMethod.Post, "/api/ward/enrollments/1/decision"),
        (HttpMethod.Get, "/api/ward/rental-applications"),
        (HttpMethod.Get, "/api/ward/rental-applications/1"),
        (HttpMethod.Post, "/api/ward/rental-applications/1/decision"),
        (HttpMethod.Get, "/api/ward/renewals"),
        (HttpMethod.Get, "/api/ward/renewals/1"),
        (HttpMethod.Post, "/api/ward/renewals/1/decision"),
        (HttpMethod.Post, "/api/ward/renewals/batch-decision"),
        (HttpMethod.Post, "/api/ward/permits/inspect"),
        (HttpMethod.Post, "/api/ward/permits/1/action"),
        (HttpMethod.Get, "/api/ward/penalty-schedules"),
        (HttpMethod.Get, "/api/ward/violations"),
        (HttpMethod.Get, "/api/ward/violations/1"),
        (HttpMethod.Get, "/api/ward/violations/1/document"),
        (HttpMethod.Post, "/api/ward/violations"),
        (HttpMethod.Post, "/api/ward/violations/1/explanation"),
        (HttpMethod.Post, "/api/ward/violations/1/deliver"),
        (HttpMethod.Post, "/api/ward/violations/1/sanction"),
        (HttpMethod.Post, "/api/ward/ai/document-extract"),
        (HttpMethod.Post, "/api/ward/ai/encroachment-check"),
        (HttpMethod.Get, "/api/ward/insights/risk-queue"),
        (HttpMethod.Get, "/api/ward/insights/patrol-heatmap"),
        (HttpMethod.Get, "/api/ward/penalty-schedules/overview"),
        (HttpMethod.Get, "/api/ward/penalty-schedules/history?violationType=X"),
        (HttpMethod.Post, "/api/ward/penalty-schedules"),
        (HttpMethod.Delete, "/api/ward/penalty-schedules/1"),
        (HttpMethod.Get, "/api/ward/pricing-zones"),
        (HttpMethod.Get, "/api/ward/pricing-zones/1"),
        (HttpMethod.Post, "/api/ward/pricing-zones"),
        (HttpMethod.Put, "/api/ward/pricing-zones/1"),
        (HttpMethod.Delete, "/api/ward/pricing-zones/1?versionToken=x"),
        (HttpMethod.Post, "/api/ward/pricing-zones/1/impact-preview"),
        (HttpMethod.Get, "/api/ward/pricing-zones/1/history"),
        (HttpMethod.Get, "/api/ward/slot-grid"),
        (HttpMethod.Post, "/api/ward/slot-grid/check"),
        (HttpMethod.Post, "/api/ward/slot-grid"),
        (HttpMethod.Put, "/api/ward/slot-grid/1"),
        (HttpMethod.Put, "/api/ward/slot-grid/1/status"),
        (HttpMethod.Delete, "/api/ward/slot-grid/1?versionToken=x"),
        (HttpMethod.Post, "/api/ward/slot-grid/batch-preview"),
        (HttpMethod.Post, "/api/ward/slot-grid/batch"),
        (HttpMethod.Get, "/api/ward/slot-grid/1/history"),
        (HttpMethod.Post, "/api/ward/street-features"),
        (HttpMethod.Put, "/api/ward/street-features/1"),
        (HttpMethod.Delete, "/api/ward/street-features/1?versionToken=x"),
    ];

    private static readonly string[] PublicAuthEndpoints =
    [
        "/api/auth/send-otp",
        "/api/auth/register",
        "/api/auth/login",
        "/api/auth/login-otp",
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
            // Development-only minimal-API endpoints (ward dev session, finance dev shortcuts)
            // are not part of the production controller contract this test pins down.
            .Where(path => !path.Name.StartsWith("/api/dev/", StringComparison.Ordinal))
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
            if (endpoint.Path is "/api/uploads/evidence" or "/api/uploads/menu-images")
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
