using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using StreetBiz.Application.Features.WardSlots;

namespace StreetBiz.API.Controllers;

[ApiController]
[Authorize]
[Route("api/ward")]
public sealed class WardReviewsController(ISender sender) : ControllerBase
{
    [HttpGet("me")]
    public async Task<ActionResult<WardProfile>> Profile(CancellationToken cancellationToken) =>
        Ok(await sender.Send(new GetWardProfileQuery(), cancellationToken));

    [HttpGet("cases/{kind}")]
    public async Task<ActionResult<CasePage>> List(
        string kind,
        [FromQuery] int page = 1,
        CancellationToken cancellationToken = default) =>
        Ok(await sender.Send(new ListWardCasesQuery(kind, page), cancellationToken));

    [HttpGet("cases/{kind}/{id:long}")]
    public async Task<ActionResult<WardCase>> Get(
        string kind,
        long id,
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(new GetWardCaseQuery(kind, id), cancellationToken));

    [HttpPost("cases/{kind}/{id:long}/decision")]
    public async Task<ActionResult<WardCase>> Decide(
        string kind,
        long id,
        WardDecisionRequest request,
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(new DecideWardCaseCommand(
            kind,
            id,
            request.Decision,
            request.Reason,
            request.ExpectedStatus), cancellationToken));

    [HttpPut("cases/proposals/{id:long}/location")]
    public async Task<ActionResult<WardCase>> Pin(
        long id,
        WardLocationRequest request,
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(
            new PinWardProposalCommand(id, request.Latitude, request.Longitude),
            cancellationToken));

    [HttpGet("geo/search")]
    [EnableRateLimiting("WardGeo")]
    public async Task<ActionResult<IReadOnlyList<GeocodeResult>>> Search(
        [FromQuery] string address,
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(new SearchWardLocationsQuery(address), cancellationToken));

    [HttpPost("geo/verify")]
    [EnableRateLimiting("WardGeo")]
    public async Task<ActionResult<GeofenceResult>> Verify(
        WardLocationRequest request,
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(
            new VerifyWardLocationQuery(request.Latitude, request.Longitude),
            cancellationToken));
}

public sealed record WardDecisionRequest(
    string Decision,
    string Reason,
    string ExpectedStatus);

public sealed record WardLocationRequest(double Latitude, double Longitude);
