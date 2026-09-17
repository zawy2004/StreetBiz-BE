using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.AddressChanges;
using StreetBiz.Application.Features.AddressChanges.RequestAddressChange;
using StreetBiz.Application.Features.AddressChanges.TrackAddressChanges;

namespace StreetBiz.API.Controllers;

[ApiController]
[Authorize]
[Route("api/vendor/address-changes")]
public sealed class AddressChangesController(ISender sender) : ControllerBase
{
    /// <summary>SIDE-09/10: request a business-address change, optionally releasing an adjacent slot.</summary>
    [HttpPost]
    public async Task<ActionResult<AddressChangeRequestDto>> Submit(
        RequestAddressChangeRequest request, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new RequestAddressChangeCommand(
            request.RegistrationId, request.NewAddress, request.NewLatitude, request.NewLongitude,
            request.ReleasedContractId, request.RequestedNewSlotId), cancellationToken);
        return Ok(new { message = SideMessages.AddressChangeRequested, data = result });
    }

    /// <summary>SIDE-09: track the caller's own address change requests.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AddressChangeRequestDto>>> Track(CancellationToken cancellationToken)
        => Ok(await sender.Send(new TrackAddressChangesQuery(), cancellationToken));
}
