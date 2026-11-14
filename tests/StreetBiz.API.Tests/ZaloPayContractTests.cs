using System.Text;
using System.Text.Json;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Moq;
using StreetBiz.API.Controllers;
using StreetBiz.API.Hubs;
using StreetBiz.Application.DTOs.Commerce;
using StreetBiz.Application.Features.Commerce;

namespace StreetBiz.API.Tests;

/// <summary>What ZaloPay is told after its callback, and when it is offered to buyers at all.</summary>
public sealed class ZaloPayContractTests
{
    private static PaymentCallbacksController Callbacks(string result, string body = "{}")
    {
        var sender = new Mock<ISender>();
        sender.Setup(x => x.Send(It.IsAny<ProcessPaymentCallbackCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentCallbackReceiptDto(result, 1, 2, null, null));
        return new PaymentCallbacksController(sender.Object, Mock.Of<IOrderRealtimePublisher>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { Request = { Body = new MemoryStream(Encoding.UTF8.GetBytes(body)) } },
            },
        };
    }

    private static string Json(object? value) => JsonSerializer.Serialize(value);

    [Theory]
    [InlineData("APPLIED", 1)]
    [InlineData("DUPLICATE", 2)]   // already processed: ZaloPay stops retrying
    [InlineData("REJECTED", -1)]   // bad MAC or amount: ZaloPay calls again
    [InlineData("UNMATCHED", -1)]
    public async Task ZaloPay_gets_its_own_return_code_for_each_outcome(string outcome, int expectedCode)
    {
        var response = await Callbacks(outcome).Callback("zalopay", default);

        var body = JsonDocument.Parse(Json(Assert.IsType<ObjectResult>(response.Result).Value)).RootElement;
        Assert.Equal(expectedCode, body.GetProperty("return_code").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("return_message").GetString()));
    }

    [Fact]
    public async Task The_provider_name_in_the_url_is_not_case_sensitive()
    {
        var response = await Callbacks("APPLIED").Callback("ZaloPay", default);

        var body = JsonDocument.Parse(Json(Assert.IsType<ObjectResult>(response.Result).Value)).RootElement;
        Assert.Equal(1, body.GetProperty("return_code").GetInt32());
    }

    [Fact]
    public async Task MoMo_is_still_acknowledged_with_204_and_other_providers_with_the_receipt()
    {
        Assert.IsType<NoContentResult>((await Callbacks("APPLIED").Callback("momo", default)).Result);

        var other = await Callbacks("APPLIED").Callback("other", default);
        Assert.Equal("APPLIED", Assert.IsType<PaymentCallbackReceiptDto>(Assert.IsType<OkObjectResult>(other.Result).Value).Result);
    }

    private static OrderPaymentsController Options(Dictionary<string, string?> settings, string environmentName = "Production")
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(x => x.EnvironmentName).Returns(environmentName);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new OrderPaymentsController(environment.Object, configuration, Mock.Of<ISender>(),
            Mock.Of<IOrderPaymentTesting>(), Mock.Of<IOrderRealtimePublisher>());
    }

    private static JsonElement PaymentOptions(OrderPaymentsController controller) =>
        JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(controller.Options()).Value);

    private static Dictionary<string, string?> ZaloPay(bool enabled, string? key2 = "k2") => new()
    {
        ["Payments:ZaloPay:Enabled"] = enabled.ToString(),
        ["Payments:ZaloPay:AppId"] = "2553",
        ["Payments:ZaloPay:Key1"] = "k1",
        ["Payments:ZaloPay:Key2"] = key2,
    };

    [Fact]
    public void ZaloPay_is_offered_only_when_it_is_switched_on_with_all_its_keys()
    {
        var on = PaymentOptions(Options(ZaloPay(enabled: true)));
        var off = PaymentOptions(Options(ZaloPay(enabled: false)));
        var half = PaymentOptions(Options(ZaloPay(enabled: true, key2: "")));

        Assert.Equal(["ZALOPAY"], on.GetProperty("providers").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal("LIVE", on.GetProperty("mode").GetString());
        Assert.Contains("ZaloPay", on.GetProperty("message").GetString());
        Assert.Equal("UNAVAILABLE", off.GetProperty("mode").GetString());
        Assert.Equal("UNAVAILABLE", half.GetProperty("mode").GetString());
    }

    [Fact]
    public void Both_gateways_can_be_live_together()
    {
        var settings = ZaloPay(enabled: true);
        settings["Payments:Momo:PartnerCode"] = "MOMO";
        settings["Payments:Momo:SecretKey"] = "secret";

        var options = PaymentOptions(Options(settings));

        Assert.Equal(["MOMO", "ZALOPAY"], options.GetProperty("providers").EnumerateArray().Select(p => p.GetString()));
    }
}
