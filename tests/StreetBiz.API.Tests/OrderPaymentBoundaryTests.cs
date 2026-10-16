using System.Text.Json;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Moq;
using StreetBiz.API.Controllers;
using StreetBiz.API.Hubs;
using StreetBiz.Application.Features.Commerce;

namespace StreetBiz.API.Tests;

public sealed class OrderPaymentBoundaryTests
{
    [Theory]
    [InlineData("Production", true)]
    [InlineData("Testing", true)]
    [InlineData("Development", false)]
    public async Task Sandbox_mutations_are_disabled_outside_explicit_development(string environmentName, bool enabled)
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(x => x.EnvironmentName).Returns(environmentName);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Payments:SandboxEnabled"] = enabled.ToString() }).Build();
        var sender = new Mock<ISender>(MockBehavior.Strict);
        var testing = new Mock<IOrderPaymentTesting>(MockBehavior.Strict);
        var realtime = Mock.Of<IOrderRealtimePublisher>();
        var payments = new OrderPaymentsController(
            environment.Object, configuration, sender.Object, testing.Object, realtime);
        Assert.IsType<NotFoundResult>(await payments.Fail(1, default));
        Assert.IsType<NotFoundResult>(await payments.Refund(1, default));
        var orders = new OrdersController(
            sender.Object, environment.Object, configuration, Mock.Of<IOrderRealtimePublisher>());
        Assert.IsType<NotFoundResult>((await orders.ConfirmSandboxPayment(1, default)).Result);
        var options = JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(payments.Options()).Value);
        Assert.Contains("UNAVAILABLE", options);
        sender.VerifyNoOtherCalls();
        testing.VerifyNoOtherCalls();
    }

    [Fact]
    public void Development_options_explicitly_disclose_simulated_payment()
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(x => x.EnvironmentName).Returns("Development");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Payments:SandboxEnabled"] = "true" }).Build();
        var controller = new OrderPaymentsController(environment.Object, configuration,
            Mock.Of<ISender>(), Mock.Of<IOrderPaymentTesting>(), Mock.Of<IOrderRealtimePublisher>());
        var options = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(controller.Options()).Value);
        Assert.Equal("SANDBOX", options.GetProperty("mode").GetString());
        Assert.Equal(["MOMO", "ZALOPAY", "SEPAY"],
            options.GetProperty("providers").EnumerateArray().Select(p => p.GetString()));
        Assert.Contains("không trừ tiền thật", options.GetProperty("message").GetString());
    }

    [Fact]
    public void SePay_is_offered_outside_development_once_its_account_is_configured()
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(x => x.EnvironmentName).Returns("Production");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Payments:SePay:BankName"] = "MBBank",
            ["Payments:SePay:AccountNumber"] = "0123456789",
        }).Build();
        var controller = new OrderPaymentsController(environment.Object, configuration,
            Mock.Of<ISender>(), Mock.Of<IOrderPaymentTesting>(), Mock.Of<IOrderRealtimePublisher>());

        var options = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(controller.Options()).Value);

        Assert.Equal(["SEPAY"], options.GetProperty("providers").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal("LIVE", options.GetProperty("mode").GetString());
        Assert.DoesNotContain("không trừ tiền thật", options.GetProperty("message").GetString());
    }
}
