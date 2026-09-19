using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Sidewalk;
using StreetBiz.Application.Features.SlotHolds.CreateSlotHold;
using StreetBiz.Application.Features.SlotHolds.ListSlotHolds;
using StreetBiz.Application.Features.SlotHolds.ReleaseSlotHold;

namespace StreetBiz.API.Controllers;

[ApiController]
[Authorize]
[Route("api/vendor/slot-holds")]
public sealed class SlotHoldsController(ISender sender) : ControllerBase
{
    /// <summary>Hold an available slot for a short while (or renew a hold the caller already has).</summary>
    [HttpPost]
    public async Task<ActionResult<SlotHoldDto>> Create(CreateSlotHoldRequest request, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new CreateSlotHoldCommand(request.RegistrationId, request.SlotId), cancellationToken);
        return Ok(new { message = SideMessages.SlotHeld, data = result });
    }

    /// <summary>List the registration's live holds.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<SlotHoldDto>>> List(
        [FromQuery] long registrationId, CancellationToken cancellationToken)
        => Ok(await sender.Send(new ListSlotHoldsQuery(registrationId), cancellationToken));

    /// <summary>Release a hold before it lapses.</summary>
    [HttpDelete("{slotId:long}")]
    public async Task<IActionResult> Release(
        long slotId, [FromQuery] long registrationId, CancellationToken cancellationToken)
    {
        await sender.Send(new ReleaseSlotHoldCommand(registrationId, slotId), cancellationToken);
        return Ok(new { message = SideMessages.SlotHoldReleased });
    }
}

public sealed record CreateSlotHoldRequest(long RegistrationId, long SlotId);
