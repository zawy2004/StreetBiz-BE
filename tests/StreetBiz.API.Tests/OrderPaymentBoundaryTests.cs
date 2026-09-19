using System.Text.Json;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Moq;
using StreetBiz.API.Controllers;
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
        var payments = new OrderPaymentsController(environment.Object, configuration, sender.Object, testing.Object);
        Assert.IsType<NotFoundResult>(await payments.Fail(1, default));
        Assert.IsType<NotFoundResult>(await payments.Refund(1, default));
        var orders = new OrdersController(sender.Object, environment.Object, configuration);
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
            Mock.Of<ISender>(), Mock.Of<IOrderPaymentTesting>());
        var options = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(controller.Options()).Value);
        Assert.Equal("SANDBOX", options.GetProperty("mode").GetString());
        Assert.Equal(2, options.GetProperty("providers").GetArrayLength());
        Assert.Contains("không trừ tiền thật", options.GetProperty("message").GetString());
    }
}
