using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Features.Tax;

namespace StreetBiz.API.Controllers;

/// <summary>
/// A household's tax picture: where its revenue stands against the no-tax threshold, the revenue
/// book it keeps, its tax profile and what the ward may see, an estimate of tax, and the notices
/// it confirms it has filed. StreetBiz files and collects nothing. Every route is scoped to the
/// caller's own household.
/// </summary>
[ApiController]
[Authorize]
[Route("api/vendor/tax")]
public sealed class TaxController(ISender sender, TimeProvider clock) : ControllerBase
{
    /// <summary>Revenue to date by source, group, threshold level, forecast, obligations and next deadline.</summary>
    [HttpGet("overview")]
    public async Task<ActionResult<TaxOverviewDto>> Overview([FromQuery] int? year, CancellationToken cancellationToken) =>
        Ok(await sender.Send(new GetTaxOverviewQuery(year), cancellationToken));

    /// <summary>One month of the revenue book, day by day (default: this month).</summary>
    [HttpGet("ledger")]
    public async Task<ActionResult<RevenueLedgerDto>> Ledger(
        [FromQuery] int? year, [FromQuery] int? month, CancellationToken cancellationToken)
    {
        var today = BusinessCalendar.Today(clock);
        return Ok(await sender.Send(new GetRevenueLedgerQuery(year ?? today.Year, month ?? today.Month), cancellationToken));
    }

    /// <summary>Records sales taken outside StreetBiz orders (cash at the stall, a transfer to the household's own account).</summary>
    [HttpPost("revenue-entries")]
    public async Task<ActionResult<RevenueEntryDto>> AddEntry(
        AddRevenueEntryRequest request, CancellationToken cancellationToken) =>
        Ok(await sender.Send(new AddRevenueEntryCommand(request), cancellationToken));

    /// <summary>Voids an entry (it stays in the book, struck through, and stops counting).</summary>
    [HttpPost("revenue-entries/{entryId:long}/void")]
    public async Task<ActionResult<RevenueEntryDto>> VoidEntry(
        long entryId,
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] VoidRevenueEntryRequest? request,
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(new VoidRevenueEntryCommand(entryId, request?.Reason), cancellationToken));

    /// <summary>The household's tax profile: tax code, industry, e-invoice, and whether the ward may see its revenue.</summary>
    [HttpGet("profile")]
    public async Task<ActionResult<TaxProfileDto>> Profile(CancellationToken cancellationToken) =>
        Ok(await sender.Send(new GetTaxProfileQuery(), cancellationToken));

    [HttpPut("profile")]
    public async Task<ActionResult<TaxProfileDto>> UpdateProfile(
        UpdateTaxProfileRequest request, CancellationToken cancellationToken) =>
        Ok(await sender.Send(new UpdateTaxProfileCommand(request), cancellationToken));

    /// <summary>
    /// What the household would owe at its revenue (ACTUAL so far, PROJECTED to year end, or a given
    /// <c>revenue</c> to try), and by the profit method when <c>costRatio</c> (0-0.99) is given.
    /// </summary>
    [HttpGet("estimate")]
    public async Task<ActionResult<TaxEstimateDto>> Estimate(
        [FromQuery] int? year,
        [FromQuery] string? basis,
        [FromQuery] decimal? revenue,
        [FromQuery] decimal? costRatio,
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(new GetTaxEstimateQuery(year, basis, revenue, costRatio), cancellationToken));

    /// <summary>The yearly revenue notice and the four quarters, with their status and the household's confirmed filings.</summary>
    [HttpGet("declarations")]
    public async Task<ActionResult<TaxDeclarationsDto>> Declarations(
        [FromQuery] int? year, CancellationToken cancellationToken) =>
        Ok(await sender.Send(new ListTaxDeclarationsQuery(year), cancellationToken));

    /// <summary>The household confirms it filed this period with the tax authority (YEAR/0 or QUARTER/1-4).</summary>
    [HttpPut("declarations/{year:int}/{periodType}/{periodNo:int}")]
    public async Task<ActionResult<TaxPeriodDto>> ConfirmFiling(
        int year, string periodType, int periodNo, ConfirmTaxFilingRequest request, CancellationToken cancellationToken) =>
        Ok(await sender.Send(
            new ConfirmTaxFilingCommand(year, periodType.ToUpperInvariant(), periodNo, request), cancellationToken));

    /// <summary>Takes back a confirmation (it was a mistake).</summary>
    [HttpDelete("declarations/{year:int}/{periodType}/{periodNo:int}")]
    public async Task<IActionResult> RetractFiling(
        int year, string periodType, int periodNo, CancellationToken cancellationToken)
    {
        await sender.Send(new DeleteTaxFilingCommand(year, periodType.ToUpperInvariant(), periodNo), cancellationToken);
        return NoContent();
    }

    /// <summary>The year's revenue book as an .xlsx workbook.</summary>
    [HttpGet("revenue-book/export")]
    public async Task<IActionResult> ExportRevenueBook([FromQuery] int? year, CancellationToken cancellationToken)
    {
        var file = await sender.Send(new ExportRevenueBookQuery(year), cancellationToken);
        return File(file.Content, file.ContentType, file.FileName);
    }

    /// <summary>The year's revenue summary as a PDF, to have beside the portal when notifying the tax office.</summary>
    [HttpGet("summary/export")]
    public async Task<IActionResult> ExportSummary([FromQuery] int? year, CancellationToken cancellationToken)
    {
        var file = await sender.Send(new ExportRevenueSummaryQuery(year), cancellationToken);
        return File(file.Content, file.ContentType, file.FileName);
    }
}
