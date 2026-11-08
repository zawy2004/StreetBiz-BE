using System.Net;
using System.Text;
using System.Text.Json;
using MediatR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Moq;
using StreetBiz.API.Controllers;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.DTOs.Commerce;
using StreetBiz.Application.Features.Commerce;
using StreetBiz.Application.Features.PayoutAccounts;

namespace StreetBiz.API.Tests;

/// <summary>
/// payOS at the HTTP edge: the webhook URL carries the payee's token, is anonymous and always 200
/// once recorded; the payment methods offered name the payee and appear only for a connected one.
/// </summary>
public sealed class PayOsEndpointTests
{
    private const string SigningKey = "test-only-signing-key-at-least-32-characters";
    private const string UnusedConnection =
        "Server=unused;Database=unused;Integrated Security=True;TrustServerCertificate=True";

    [Fact]
    public async Task The_payos_webhook_passes_its_url_token_and_raw_body_to_the_handler_and_answers_200_success()
    {
        ProcessPaymentCallbackCommand? received = null;
        var sender = new Mock<ISender>();
        sender.Setup(x => x.Send(It.IsAny<ProcessPaymentCallbackCommand>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<PaymentCallbackReceiptDto>, CancellationToken>((command, _) => received = (ProcessPaymentCallbackCommand)command)
            .ReturnsAsync(new PaymentCallbackReceiptDto("APPLIED", 7, 101, null, null));
        using var factory = Host(sender.Object);
        using var client = factory.CreateClient();
        const string body = """{"code":"00","success":true,"data":{"orderCode":101001,"amount":1040000},"signature":"abc"}""";

        // No Authorization header at all: payOS authenticates by signature, not by a bearer token.
        using var response = await client.PostAsync(
            "/api/payments/payos/callback/the-token_123", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("APPLIED", json.RootElement.GetProperty("result").GetString());
        Assert.Equal("PAYOS", received!.Provider);
        Assert.Equal("the-token_123", received.PayeeToken);
        Assert.Equal(body, received.RawPayload);
    }

    [Fact]
    public async Task The_generic_callback_route_gives_payos_no_token_so_it_settles_nothing()
    {
        ProcessPaymentCallbackCommand? received = null;
        var sender = new Mock<ISender>();
        sender.Setup(x => x.Send(It.IsAny<ProcessPaymentCallbackCommand>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<PaymentCallbackReceiptDto>, CancellationToken>((command, _) => received = (ProcessPaymentCallbackCommand)command)
            .ReturnsAsync(new PaymentCallbackReceiptDto("IGNORED", 0, null, null, null));
        using var factory = Host(sender.Object);
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/api/payments/payos/callback", new StringContent("{}", Encoding.UTF8, "application/json"));

        // It reaches the generic route, but with no token the handler has no channel to verify with
        // (see the handler tests): such a report is ignored and can never settle a payment.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("payos", received!.Provider);
        Assert.Null(received.PayeeToken);
    }

    // ----------------------------------------------------------------------- payment options

    private static readonly PayeeSummary Ward = new(11, "WARD", "Phường Hải Châu", "MB Bank", "••••••6789", "UBND PHUONG", true);

    private static JsonElement Options(bool sandbox, PayeeSummary? payee, Dictionary<string, string?>? config = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config ?? []).Build();
        return JsonSerializer.SerializeToElement(PaymentOptions.Build(sandbox, configuration, payee));
    }

    private static string?[] Providers(JsonElement options) =>
        [.. options.GetProperty("providers").EnumerateArray().Select(p => p.GetString())];

    [Fact]
    public void A_connected_payee_makes_payos_the_first_choice_and_is_named_to_the_payer()
    {
        var options = Options(sandbox: false, Ward);

        Assert.Equal(["PAYOS"], Providers(options));
        Assert.Equal("LIVE", options.GetProperty("mode").GetString());
        var payee = options.GetProperty("payee");
        Assert.Equal("Phường Hải Châu", payee.GetProperty("name").GetString());
        Assert.Equal("••••••6789", payee.GetProperty("accountNumberMasked").GetString());
        Assert.False(payee.TryGetProperty("apiKey", out _));
        Assert.Contains("VietQR", options.GetProperty("message").GetString());
    }

    [Fact]
    public void Without_a_connected_payee_there_is_no_payos_and_nothing_online_when_nothing_else_is_configured()
    {
        var none = Options(sandbox: false, null);
        var inError = Options(sandbox: false, Ward with { Active = false });

        Assert.Empty(Providers(none));
        Assert.Equal("UNAVAILABLE", none.GetProperty("mode").GetString());
        Assert.Empty(Providers(inError));
        Assert.Equal(JsonValueKind.Null, none.GetProperty("payee").ValueKind);
    }

    [Fact]
    public void Payos_comes_before_the_platform_providers_and_can_be_switched_off_on_the_server()
    {
        var sandbox = Options(sandbox: true, Ward);
        var off = Options(sandbox: true, Ward, new() { ["Payments:PayOs:Enabled"] = "false" });

        Assert.Equal(["PAYOS", "MOMO", "ZALOPAY"], Providers(sandbox));
        Assert.Equal(["MOMO", "ZALOPAY"], Providers(off));
        Assert.Equal(JsonValueKind.Null, off.GetProperty("payee").ValueKind);
    }

    [Fact]
    public void The_platform_wide_options_are_unchanged_when_there_is_no_payee()
    {
        var configured = Options(sandbox: false, null, new()
        {
            ["Payments:Momo:PartnerCode"] = "p", ["Payments:Momo:SecretKey"] = "s",
        });

        Assert.Equal(["MOMO"], Providers(configured));
        Assert.Equal("LIVE", configured.GetProperty("mode").GetString());
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
