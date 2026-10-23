using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StreetBiz.Application.Features.Commerce;
using StreetBiz.API.Hubs;

namespace StreetBiz.API.Controllers;

[ApiController]
[Authorize]
[Route("api/orders")]
public sealed class OrderPaymentsController(IHostEnvironment environment, IConfiguration configuration,
    ISender sender, IOrderPaymentTesting testing, IOrderRealtimePublisher realtime) : ControllerBase
{
    private bool SandboxEnabled => environment.IsDevelopment() && configuration.GetValue<bool>("Payments:SandboxEnabled");

    /// <summary>MoMo's own gateway is used whenever its merchant credentials are configured.</summary>
    private bool MomoLive =>
        !string.IsNullOrWhiteSpace(configuration["Payments:Momo:PartnerCode"])
        && !string.IsNullOrWhiteSpace(configuration["Payments:Momo:SecretKey"]);

    /// <summary>ZaloPay's gateway is offered only when it is switched on and has the credentials to sign a request.</summary>
    private bool ZaloPayLive =>
        configuration.GetValue<bool>("Payments:ZaloPay:Enabled")
        && !string.IsNullOrWhiteSpace(configuration["Payments:ZaloPay:AppId"])
        && !string.IsNullOrWhiteSpace(configuration["Payments:ZaloPay:Key1"])
        && !string.IsNullOrWhiteSpace(configuration["Payments:ZaloPay:Key2"]);

    [HttpGet("payment-options")]
    public IActionResult Options()
    {
        // The simulator is only ever offered in Development; a real gateway is offered wherever it is configured.
        var momoLive = MomoLive;
        var zaloLive = ZaloPayLive;
        var providers = SandboxEnabled
            ? new[] { "MOMO", "ZALOPAY" }
            : new[] { momoLive ? "MOMO" : null, zaloLive ? "ZALOPAY" : null }.OfType<string>().ToArray();
        var live = momoLive || zaloLive;
        return Ok(new
        {
            mode = providers.Length == 0 ? "UNAVAILABLE" : live ? "LIVE" : "SANDBOX",
            providers,
            message = providers.Length == 0
                ? "Thanh toán trực tuyến chưa sẵn sàng. Vui lòng thử lại sau."
                : live
                    ? "Thanh toán qua cổng " + string.Join(" / ", providers.Select(p => p == "MOMO" ? "MoMo" : "ZaloPay"))
                        + " (môi trường test, không trừ tiền thật)."
                    : "Thanh toán thử nghiệm, không trừ tiền thật."
        });
    }

    [HttpPost("{orderId:long}/payment/sandbox-fail")]
    public async Task<IActionResult> Fail(long orderId, CancellationToken ct)
    {
        if (!SandboxEnabled) return NotFound();
        await testing.Fail(orderId, ct);
        var result = await sender.Send(new GetCustomerOrderQuery(orderId), ct);
        await realtime.PublishAsync(result, ct);
        return Ok(result);
    }

    [HttpPost("{orderId:long}/refund/sandbox-confirm")]
    public async Task<IActionResult> Refund(long orderId, CancellationToken ct)
    {
        if (!SandboxEnabled) return NotFound();
        await testing.Refund(orderId, ct);
        var result = await sender.Send(new GetCustomerOrderQuery(orderId), ct);
        await realtime.PublishAsync(result, ct);
        return Ok(result);
    }
}
