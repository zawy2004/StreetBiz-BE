using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Sidewalk;
using StreetBiz.Application.Features.SlotProposals.ProposeSlot;
using StreetBiz.Application.Features.SlotProposals.TrackProposals;

namespace StreetBiz.API.Controllers;

[ApiController]
[Authorize]
[Route("api/vendor/slot-proposals")]
public sealed class SlotProposalsController(ISender sender) : ControllerBase
{
    /// <summary>SIDE-11: propose a new sidewalk slot at an address not on the ward's grid.</summary>
    [HttpPost]
    public async Task<ActionResult<SlotProposalDto>> Propose(ProposeSlotRequest request, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new ProposeSlotCommand(
            request.RegistrationId, request.ZoneId, request.Latitude, request.Longitude,
            request.WidthMeters, request.LengthMeters, request.ProposalPhotoUrl), cancellationToken);
        return Ok(new { message = SideMessages.SlotProposed, data = result });
    }

    /// <summary>SIDE-11: track the caller's own proposed slots and their review status.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<SlotProposalDto>>> Track(CancellationToken cancellationToken)
        => Ok(await sender.Send(new TrackProposalsQuery(), cancellationToken));
}
