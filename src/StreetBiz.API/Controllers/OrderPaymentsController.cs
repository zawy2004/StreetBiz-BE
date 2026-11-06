using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StreetBiz.Application.DTOs.Payments;
using StreetBiz.Application.Features.Commerce;
using StreetBiz.Application.Features.PayoutAccounts;
using StreetBiz.API.Hubs;

namespace StreetBiz.API.Controllers;

[ApiController]
[Authorize]
[Route("api/orders")]
public sealed class OrderPaymentsController(IHostEnvironment environment, IConfiguration configuration,
    ISender sender, IOrderPaymentTesting testing, IOrderRealtimePublisher realtime) : ControllerBase
{
    private bool SandboxEnabled => environment.IsDevelopment() && configuration.GetValue<bool>("Payments:SandboxEnabled");

    /// <summary>The payment methods the platform offers, whoever is being paid.</summary>
    [HttpGet("payment-options")]
    public IActionResult Options() => Ok(PaymentOptions.Build(SandboxEnabled, configuration));

    /// <summary>
    /// The payment methods for an order from this storefront. PAYOS is offered, naming the household
    /// that would be paid, only when the household has connected its own receiving account; without
    /// it the storefront takes no online order by bank transfer.
    /// </summary>
    [HttpGet("storefronts/{storefrontId:long}/payment-options")]
    public async Task<IActionResult> StorefrontOptions(long storefrontId, CancellationToken ct) =>
        Ok(PaymentOptions.Build(
            SandboxEnabled, configuration, await sender.Send(new GetStorefrontPayeeQuery(storefrontId), ct)));

    /// <summary>The VietQR and transfer details of an order still waiting for its SePay bank transfer.</summary>
    [HttpGet("{orderId:long}/payment/bank-transfer")]
    public async Task<ActionResult<BankTransferDto>> BankTransfer(long orderId, CancellationToken ct) =>
        Ok(await sender.Send(new GetOrderBankTransferQuery(orderId), ct));

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
