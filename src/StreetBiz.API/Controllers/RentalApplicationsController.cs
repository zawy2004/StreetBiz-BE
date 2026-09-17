using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.RentalApplications;
using StreetBiz.Application.Features.RentalApplications.SubmitAdjacentApplication;
using StreetBiz.Application.Features.RentalApplications.SubmitOpenSlotApplication;

namespace StreetBiz.API.Controllers;

[ApiController]
[Authorize]
[Route("api/vendor/rental-applications")]
public sealed class RentalApplicationsController(ISender sender) : ControllerBase
{
    /// <summary>SIDE-03B: submit an application for an open slot chosen from the map.</summary>
    [HttpPost("open-slot")]
    public async Task<ActionResult<RentalApplicationDto>> SubmitOpenSlot(
        SubmitOpenSlotApplicationRequest request, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new SubmitOpenSlotApplicationCommand(
            request.RegistrationId, request.SlotId, request.RequestedTermDays), cancellationToken);
        return Ok(new { message = SideMessages.ApplicationSubmitted, data = result });
    }

    /// <summary>SIDE-03A: submit a storefront-adjacent application.</summary>
    [HttpPost("adjacent")]
    public async Task<ActionResult<RentalApplicationDto>> SubmitAdjacent(
        SubmitAdjacentApplicationRequest request, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new SubmitAdjacentApplicationCommand(
            request.RegistrationId, request.SlotId, request.RequestedTermDays), cancellationToken);
        return Ok(new { message = SideMessages.ApplicationSubmitted, data = result });
    }
}
