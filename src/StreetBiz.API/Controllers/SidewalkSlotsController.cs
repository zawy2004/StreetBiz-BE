using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StreetBiz.Application.DTOs.Sidewalk;
using StreetBiz.Application.Features.SidewalkSlots.GetSlot;
using StreetBiz.Application.Features.SidewalkSlots.SearchSlots;

namespace StreetBiz.API.Controllers;

[ApiController]
[Authorize]
[Route("api/sidewalk-slots")]
public sealed class SidewalkSlotsController(ISender sender) : ControllerBase
{
    /// <summary>SIDE-01: browse sidewalk slots within an area (center+radius or bounding box).</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<SidewalkSlotDto>>> Search(
        [FromQuery] SearchSlotsQuery query, CancellationToken cancellationToken)
        => Ok(await sender.Send(query, cancellationToken));

    /// <summary>SIDE-02: view one sidewalk slot's details.</summary>
    [HttpGet("{slotId:long}")]
    public async Task<ActionResult<SidewalkSlotDto>> Get(long slotId, CancellationToken cancellationToken)
        => Ok(await sender.Send(new GetSlotQuery(slotId), cancellationToken));
}
