using System.Net;
using System.Text;
using System.Text.Json;
using MediatR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using StreetBiz.Application.DTOs.Commerce;
using StreetBiz.Application.Features.Commerce;

namespace StreetBiz.API.Tests;

/// <summary>
/// SePay's webhook contract at the HTTP edge: anonymous, its authentication headers passed on
/// untouched, and a 200 with {"success": true} — anything else and SePay retries for hours.
/// </summary>
public sealed class SePayCallbackEndpointTests
{
    private const string SigningKey = "test-only-signing-key-at-least-32-characters";
    private const string UnusedConnection =
        "Server=unused;Database=unused;Integrated Security=True;TrustServerCertificate=True";

    [Fact]
    public async Task The_webhook_is_answered_the_way_sepay_expects_and_its_headers_reach_the_handler()
    {
        ProcessPaymentCallbackCommand? received = null;
        var sender = new Mock<ISender>();
        sender.Setup(x => x.Send(It.IsAny<ProcessPaymentCallbackCommand>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<PaymentCallbackReceiptDto>, CancellationToken>((command, _) =>
                received = (ProcessPaymentCallbackCommand)command)
            .ReturnsAsync(new PaymentCallbackReceiptDto("UNMATCHED", 7, null, null, null));
        using var factory = Host(sender.Object);
        using var client = factory.CreateClient();
        const string body = """{"id":92704,"transferType":"in","transferAmount":900000,"content":"SBP42"}""";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/payments/sepay/callback")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-SePay-Signature", "sha256=abc");
        request.Headers.Add("X-SePay-Timestamp", "1790000000");
        // An "Authorization: Apikey …" header must not trip the JWT bearer authentication.
        request.Headers.TryAddWithoutValidation("Authorization", "Apikey test-key");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("UNMATCHED", json.RootElement.GetProperty("result").GetString());
        Assert.NotNull(received);
        Assert.Equal("sepay", received!.Provider);
        Assert.Equal(body, received.RawPayload);
        Assert.Equal("sha256=abc", received.Signature);
        Assert.Equal("1790000000", received.Timestamp);
        Assert.Equal("Apikey test-key", received.Authorization);
    }

    private static WebApplicationFactory<Program> Host(ISender sender) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:StreetBizDB", UnusedConnection);
            builder.UseSetting("Jwt:SigningKey", SigningKey);
            builder.UseSetting("Jwt:Issuer", "StreetBiz");
            builder.UseSetting("Jwt:Audience", "StreetBiz");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:StreetBizDB"] = UnusedConnection,
                    ["Jwt:SigningKey"] = SigningKey,
                    ["Jwt:Issuer"] = "StreetBiz",
                    ["Jwt:Audience"] = "StreetBiz",
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ISender>();
                services.AddSingleton(sender);
            });
        });
}
