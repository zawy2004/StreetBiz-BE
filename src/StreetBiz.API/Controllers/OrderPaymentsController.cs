using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StreetBiz.Application.Features.Commerce;

namespace StreetBiz.API.Controllers;

[ApiController]
[Authorize]
[Route("api/orders")]
public sealed class OrderPaymentsController(IHostEnvironment environment, IConfiguration configuration,
    ISender sender, IOrderPaymentTesting testing) : ControllerBase
{
    private bool SandboxEnabled => environment.IsDevelopment() && configuration.GetValue<bool>("Payments:SandboxEnabled");

    [HttpGet("payment-options")]
    public IActionResult Options() => Ok(new
    {
        mode = SandboxEnabled ? "SANDBOX" : "UNAVAILABLE",
        providers = SandboxEnabled ? new[] { "MOMO", "ZALOPAY" } : [],
        message = SandboxEnabled ? "Thanh toán thử nghiệm, không trừ tiền thật." : "Thanh toán trực tuyến chưa sẵn sàng. Vui lòng thử lại sau."
    });

    [HttpPost("{orderId:long}/payment/sandbox-fail")]
    public async Task<IActionResult> Fail(long orderId, CancellationToken ct)
    {
        if (!SandboxEnabled) return NotFound();
        await testing.Fail(orderId, ct);
        return Ok(await sender.Send(new GetCustomerOrderQuery(orderId), ct));
    }

    [HttpPost("{orderId:long}/refund/sandbox-confirm")]
    public async Task<IActionResult> Refund(long orderId, CancellationToken ct)
    {
        if (!SandboxEnabled) return NotFound();
        await testing.Refund(orderId, ct);
        return Ok(await sender.Send(new GetCustomerOrderQuery(orderId), ct));
    }
}
