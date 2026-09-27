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

    [HttpGet("payment-options")]
    public IActionResult Options()
    {
        // The simulator is only ever offered in Development; MoMo is offered wherever it is configured.
        var providers = (SandboxEnabled ? new[] { "MOMO", "ZALOPAY" } : MomoLive ? ["MOMO"] : []);
        var momoLive = MomoLive;
        return Ok(new
        {
            mode = providers.Length == 0 ? "UNAVAILABLE" : momoLive ? "LIVE" : "SANDBOX",
            providers,
            message = providers.Length == 0
                ? "Thanh toán trực tuyến chưa sẵn sàng. Vui lòng thử lại sau."
                : momoLive
                    ? "Thanh toán qua cổng MoMo (môi trường test của MoMo, không trừ tiền thật)."
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
