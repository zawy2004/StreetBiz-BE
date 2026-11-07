using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StreetBiz.Application.DTOs.Finance;
using StreetBiz.Application.Features.Finance.WardReports;

namespace StreetBiz.API.Controllers;

/// <summary>
/// WARD-14 collections: who owes the ward, how punctually fees are paid, the monthly trend, and
/// the reminders an officer sends. Every route is scoped to the officer's own ward.
/// </summary>
[ApiController]
[Authorize]
[Route("api/ward/reports")]
public sealed class WardCollectionController(ISender sender) : ControllerBase
{
    /// <summary>Households with overdue rental fees, most overdue first.</summary>
    [HttpGet("debtors")]
    public async Task<ActionResult<IReadOnlyList<WardDebtorDto>>> Debtors(CancellationToken cancellationToken) =>
        Ok(await sender.Send(new ListWardDebtorsQuery(), cancellationToken));

    /// <summary>Sends the household an in-app reminder; once per contract per day.</summary>
    [HttpPost("debtors/{contractId:long}/remind")]
    public async Task<ActionResult<DebtReminderDto>> Remind(long contractId, CancellationToken cancellationToken) =>
        Ok(await sender.Send(new RemindDebtorCommand(contractId), cancellationToken));

    /// <summary>On-time rate for fees falling due in the period, and figures by zone.</summary>
    [HttpGet("performance")]
    public async Task<ActionResult<CollectionPerformanceDto>> Performance(
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(new GetCollectionPerformanceQuery(from, to), cancellationToken));

    /// <summary>Fees and penalties collected, month by month (default: the last 6 months).</summary>
    [HttpGet("trend")]
    public async Task<ActionResult<IReadOnlyList<MonthlyCollectionDto>>> Trend(
        [FromQuery] int months = 6,
        CancellationToken cancellationToken = default) =>
        Ok(await sender.Send(new GetCollectionTrendQuery(months), cancellationToken));

    /// <summary>
    /// "Lịch sử thu": every receipt of the ward in the period (default: this month), newest first,
    /// with how it was paid and the reference to check it against.
    /// </summary>
    [HttpGet("receipts")]
    public async Task<ActionResult<WardReceiptListDto>> Receipts(
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(new ListWardReceiptsQuery(from, to), cancellationToken));

    /// <summary>One receipt of the ward as the household's PDF; another ward's receipt is 404.</summary>
    [HttpGet("receipts/{invoiceId:long}/pdf")]
    public async Task<IActionResult> ReceiptPdf(long invoiceId, CancellationToken cancellationToken)
    {
        var file = await sender.Send(new GetWardReceiptPdfQuery(invoiceId), cancellationToken);
        return File(file.Content, file.ContentType, file.FileName);
    }

    /// <summary>Overdue fees by age, fees due in the next 30 days, and payments owed back.</summary>
    [HttpGet("receivables")]
    public async Task<ActionResult<ReceivablesDto>> Receivables(CancellationToken cancellationToken) =>
        Ok(await sender.Send(new GetReceivablesQuery(), cancellationToken));

    /// <summary>Payments received twice: owed back to the vendor first, then those already refunded.</summary>
    [HttpGet("refunds")]
    public async Task<ActionResult<IReadOnlyList<WardRefundDto>>> Refunds(CancellationToken cancellationToken) =>
        Ok(await sender.Send(new ListWardRefundsQuery(), cancellationToken));

    /// <summary>Records that a payment owed back has been refunded to the vendor.</summary>
    [HttpPost("refunds/{transactionId:long}/mark-refunded")]
    public async Task<ActionResult<WardRefundDto>> MarkRefunded(
        long transactionId,
        [FromBody] MarkRefundedRequest? request,
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(new MarkPaymentRefundedCommand(transactionId, request?.Note), cancellationToken));
}

/// <summary>How the refund was made, e.g. "Hoàn qua cổng MoMo ngày 10/10".</summary>
public sealed record MarkRefundedRequest(string? Note);
