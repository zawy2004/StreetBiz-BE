using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.RentalApplications;
using StreetBiz.Application.Features.RentalApplications.GetApplication;
using StreetBiz.Application.Features.RentalApplications.SubmitAdjacentApplication;
using StreetBiz.Application.Features.RentalApplications.SubmitOpenSlotApplication;
using StreetBiz.Application.Features.RentalApplications.TrackApplications;
using StreetBiz.Application.Features.RentalApplications.WithdrawApplication;

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
            request.RegistrationId, request.SlotId, request.RequestedTermDays, request.CommitmentsAccepted), cancellationToken);
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

    /// <summary>SIDE-04: list the caller's rental applications and their status.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<RentalApplicationDto>>> Track(CancellationToken cancellationToken)
        => Ok(await sender.Send(new TrackApplicationsQuery(), cancellationToken));

    /// <summary>SIDE-04: one rental application's detail.</summary>
    [HttpGet("{applicationId:long}")]
    public async Task<ActionResult<RentalApplicationDto>> Get(long applicationId, CancellationToken cancellationToken)
        => Ok(await sender.Send(new GetApplicationQuery(applicationId), cancellationToken));

    /// <summary>SIDE-04: withdraw an application that has not yet been decided.</summary>
    [HttpPost("{applicationId:long}/withdraw")]
    public async Task<IActionResult> Withdraw(long applicationId, CancellationToken cancellationToken)
    {
        await sender.Send(new WithdrawApplicationCommand(applicationId), cancellationToken);
        return Ok(new { message = SideMessages.ApplicationWithdrawn });
    }
}
