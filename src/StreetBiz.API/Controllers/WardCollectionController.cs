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
}
