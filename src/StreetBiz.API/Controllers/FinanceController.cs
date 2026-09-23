using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Finance;
using StreetBiz.Application.Features.Finance.ConfirmSandboxPayment;
using StreetBiz.Application.Features.Finance.GetInvoice;
using StreetBiz.Application.Features.Finance.ListInvoices;
using StreetBiz.Application.Features.Finance.PayFee;
using StreetBiz.Application.Features.Finance.PayPenalty;

namespace StreetBiz.API.Controllers;

public sealed record PayFeeRequest(string Provider);

public sealed record PayPenaltyRequest(string Provider);

[ApiController]
[Authorize]
[Route("api/vendor/finance")]
public sealed class FinanceController(
    ISender sender,
    IHostEnvironment environment,
    IConfiguration configuration) : ControllerBase
{
    private bool SandboxEnabled =>
        environment.IsDevelopment() && configuration.GetValue<bool>("Payments:SandboxEnabled");

    /// <summary>FEE-01: open a checkout for one fee instalment.</summary>
    [HttpPost("fees/{feeItemId:long}/checkout")]
    public async Task<ActionResult<FinanceCheckoutDto>> PayFee(
        long feeItemId,
        PayFeeRequest request,
        [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(
            new PayFeeCommand(feeItemId, request.Provider, idempotencyKey), cancellationToken));

    /// <summary>FEE-04: open a checkout for one penalty.</summary>
    [HttpPost("penalties/{penaltyId:long}/checkout")]
    public async Task<ActionResult<FinanceCheckoutDto>> PayPenalty(
        long penaltyId,
        PayPenaltyRequest request,
        [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(
            new PayPenaltyCommand(penaltyId, request.Provider, idempotencyKey), cancellationToken));

    /// <summary>
    /// Development-only stand-in for a real signed callback (same role as
    /// <c>POST /api/orders/{orderId}/payment/sandbox-fail</c>): confirms a PENDING transaction
    /// this vendor opened, without a MoMo/ZaloPay sandbox account. 404 outside Development or
    /// when Payments:SandboxEnabled is off, so it can never exist in production.
    /// </summary>
    [HttpPost("payments/{transactionId:long}/sandbox-confirm")]
    public async Task<IActionResult> ConfirmSandboxPayment(long transactionId, CancellationToken cancellationToken)
    {
        if (!SandboxEnabled)
        {
            return NotFound();
        }

        var result = await sender.Send(
            new ConfirmSandboxFinancePaymentCommand(transactionId), cancellationToken);
        return Ok(new { outcome = result.Outcome.ToString().ToUpperInvariant(), result.CallbackEventId });
    }

    /// <summary>FEE-03: the caller's invoices, most recent first.</summary>
    [HttpGet("invoices")]
    public async Task<ActionResult<IReadOnlyList<InvoiceDto>>> ListInvoices(CancellationToken cancellationToken) =>
        Ok(await sender.Send(new ListInvoicesQuery(), cancellationToken));

    /// <summary>FEE-03: one invoice's full detail.</summary>
    [HttpGet("invoices/{invoiceId:long}")]
    public async Task<ActionResult<InvoiceDetailDto>> GetInvoice(long invoiceId, CancellationToken cancellationToken) =>
        Ok(await sender.Send(new GetInvoiceQuery(invoiceId), cancellationToken));
}
