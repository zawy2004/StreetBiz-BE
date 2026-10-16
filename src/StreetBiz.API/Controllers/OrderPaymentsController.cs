using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StreetBiz.Application.DTOs.Payments;
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

    /// <summary>SePay bank transfers are offered wherever the receiving account is configured.</summary>
    private bool SePayLive =>
        !string.IsNullOrWhiteSpace(configuration["Payments:SePay:BankName"])
        && !string.IsNullOrWhiteSpace(configuration["Payments:SePay:AccountNumber"]);

    [HttpGet("payment-options")]
    public IActionResult Options()
    {
        // The simulator is only ever offered in Development; MoMo and SePay wherever they are configured.
        var providers = SandboxEnabled
            ? new[] { "MOMO", "ZALOPAY", "SEPAY" }
            : [.. MomoLive ? new[] { "MOMO" } : [], .. SePayLive ? new[] { "SEPAY" } : []];
        var momoLive = MomoLive;
        // Without the simulator, whatever is offered moves real money (SePay is a real bank transfer).
        var live = momoLive || !SandboxEnabled;
        return Ok(new
        {
            mode = providers.Length == 0 ? "UNAVAILABLE" : live ? "LIVE" : "SANDBOX",
            providers,
            message = providers.Length == 0
                ? "Thanh toán trực tuyến chưa sẵn sàng. Vui lòng thử lại sau."
                : momoLive
                    ? "Thanh toán qua cổng MoMo (môi trường test của MoMo, không trừ tiền thật)."
                    : live
                        ? "Chuyển khoản ngân hàng qua VietQR, hệ thống tự xác nhận khi tiền về."
                        : "Thanh toán thử nghiệm, không trừ tiền thật."
        });
    }

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
