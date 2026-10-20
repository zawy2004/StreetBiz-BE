using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Finance;
using StreetBiz.Application.Features.Finance.ConfirmSandboxPayment;
using StreetBiz.Application.Features.Finance.GetInvoice;
using StreetBiz.Application.Features.Finance.InvoiceDocuments;
using StreetBiz.Application.Features.Finance.GetSummary;
using StreetBiz.Application.Features.Finance.ListFeeItems;
using StreetBiz.Application.Features.Finance.ListInvoices;
using StreetBiz.Application.Features.Finance.ListPayments;
using StreetBiz.Application.Features.Finance.ListPenalties;
using StreetBiz.Application.Features.Finance.ListViolations;
using StreetBiz.Application.Features.Finance.PayFee;
using StreetBiz.Application.Features.Finance.PayPenalty;
using StreetBiz.Application.Features.Finance.SyncPayment;
using StreetBiz.Application.Features.Finance.VendorContracts;

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

    /// <summary>FinanceHome's top summary card.</summary>
    [HttpGet("summary")]
    public async Task<ActionResult<FinanceSummaryDto>> Summary(CancellationToken cancellationToken) =>
        Ok(await sender.Send(new GetFinanceSummaryQuery(), cancellationToken));

    /// <summary>FinanceHome's "Phí thuê ô" tab, optionally filtered by status (PENDING/OVERDUE/PAID).</summary>
    [HttpGet("fees")]
    public async Task<ActionResult<IReadOnlyList<FeeItemDto>>> ListFees(
        [FromQuery] string? status, CancellationToken cancellationToken) =>
        Ok(await sender.Send(new ListFeeItemsQuery(status), cancellationToken));

    /// <summary>FinanceHome's "Biên bản phạt" tab, optionally filtered by status (UNPAID/PAID/WAIVED/CANCELLED).</summary>
    [HttpGet("penalties")]
    public async Task<ActionResult<IReadOnlyList<PenaltyListDto>>> ListPenalties(
        [FromQuery] string? status, CancellationToken cancellationToken) =>
        Ok(await sender.Send(new ListPenaltiesQuery(status), cancellationToken));

    /// <summary>FEE-05: the caller's fee/penalty payment attempts, most recent first.</summary>
    [HttpGet("payments")]
    public async Task<ActionResult<IReadOnlyList<PaymentTransactionDto>>> ListPayments(
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(new ListPaymentTransactionsQuery(), cancellationToken));

    /// <summary>FEE-05: violations recorded against the caller, most recent first.</summary>
    [HttpGet("violations")]
    public async Task<ActionResult<IReadOnlyList<VendorViolationDto>>> ListViolations(
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(new ListVendorViolationsQuery(), cancellationToken));

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

    /// <summary>
    /// FEE-01/FEE-04: back from MoMo, ask MoMo for the real state of this vendor's payment
    /// and apply it. Never trusts the return URL's query string.
    /// </summary>
    [HttpPost("payments/{transactionId:long}/sync")]
    public async Task<ActionResult<FinancePaymentSyncDto>> SyncPayment(
        long transactionId, CancellationToken cancellationToken) =>
        Ok(await sender.Send(new SyncFinancePaymentCommand(transactionId), cancellationToken));

    /// <summary>FEE-03: the caller's invoices, most recent first.</summary>
    [HttpGet("invoices")]
    public async Task<ActionResult<IReadOnlyList<InvoiceDto>>> ListInvoices(CancellationToken cancellationToken) =>
        Ok(await sender.Send(new ListInvoicesQuery(), cancellationToken));

    /// <summary>FEE-03: one invoice's full detail.</summary>
    [HttpGet("invoices/{invoiceId:long}")]
    public async Task<ActionResult<InvoiceDetailDto>> GetInvoice(long invoiceId, CancellationToken cancellationToken) =>
        Ok(await sender.Send(new GetInvoiceQuery(invoiceId), cancellationToken));

    /// <summary>FEE-03: the caller's payment receipt as a PDF download.</summary>
    [HttpGet("invoices/{invoiceId:long}/pdf")]
    public async Task<IActionResult> DownloadInvoicePdf(long invoiceId, CancellationToken cancellationToken)
    {
        var file = await sender.Send(new GetInvoicePdfQuery(invoiceId), cancellationToken);
        return File(file.Content, file.ContentType, file.FileName);
    }

    /// <summary>Every contract with a fee schedule and how far it has been paid.</summary>
    [HttpGet("contracts")]
    public async Task<ActionResult<IReadOnlyList<VendorContractFinanceDto>>> ListContracts(
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(new ListVendorContractsQuery(), cancellationToken));

    /// <summary>One contract's fee schedule, instalment by instalment, with the invoices issued.</summary>
    [HttpGet("contracts/{contractId:long}/schedule")]
    public async Task<ActionResult<ContractScheduleDto>> GetContractSchedule(
        long contractId, CancellationToken cancellationToken) =>
        Ok(await sender.Send(new GetContractScheduleQuery(contractId), cancellationToken));

    /// <summary>FEE-01: one instalment and its contract, before and after it is paid.</summary>
    [HttpGet("fees/{feeItemId:long}")]
    public async Task<ActionResult<FeeItemDetailDto>> GetFeeItem(long feeItemId, CancellationToken cancellationToken) =>
        Ok(await sender.Send(new GetFeeItemDetailQuery(feeItemId), cancellationToken));

    /// <summary>A year's receipts and instalments as an .xlsx statement (default: this year).</summary>
    [HttpGet("statement")]
    public async Task<IActionResult> DownloadStatement([FromQuery] int? year, CancellationToken cancellationToken)
    {
        var file = await sender.Send(new GetVendorStatementQuery(year), cancellationToken);
        return File(file.Content, file.ContentType, file.FileName);
    }
}
