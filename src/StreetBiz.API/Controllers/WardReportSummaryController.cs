using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StreetBiz.Application.Features.Finance.WardReports;

namespace StreetBiz.API.Controllers;

/// <summary>
/// AIC-08: the WARD-14/15 numbers written out as a few sentences. Ward scoping happens inside the
/// handler via IWardActorContext, like the other ward report routes.
/// </summary>
[ApiController]
[Authorize]
[Route("api/ward/reports")]
public sealed class WardReportSummaryController(ISender sender) : ControllerBase
{
    /// <summary>AIC-08: the period's collection, debt, violations and workload in plain sentences.</summary>
    [HttpGet("summary")]
    public async Task<ActionResult<ReportSummaryDto>> Summary(
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(new GetReportSummaryQuery(from, to), cancellationToken));
}
