using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StreetBiz.Application.DTOs.Sidewalk;
using StreetBiz.Application.Features.SidewalkZones.GetZone;

namespace StreetBiz.API.Controllers;

[ApiController]
[Authorize]
[Route("api/sidewalk-zones")]
public sealed class SidewalkZonesController(ISender sender) : ControllerBase
{
    /// <summary>One pricing zone with its ward contact, fee table and street features.</summary>
    [HttpGet("{zoneId:int}")]
    public async Task<ActionResult<SidewalkZoneDto>> Get(int zoneId, CancellationToken cancellationToken)
        => Ok(await sender.Send(new GetZoneQuery(zoneId), cancellationToken));
}
