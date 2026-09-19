using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.SlotTransfers;
using StreetBiz.Application.Features.SlotTransfers.AcceptTransfer;
using StreetBiz.Application.Features.SlotTransfers.DeclineTransfer;
using StreetBiz.Application.Features.SlotTransfers.ListTransfers;
using StreetBiz.Application.Features.SlotTransfers.RequestTransfer;

namespace StreetBiz.API.Controllers;

[ApiController]
[Authorize]
[Route("api/vendor/slot-transfers")]
public sealed class SlotTransfersController(ISender sender) : ControllerBase
{
    /// <summary>SIDE-12: request to transfer an active contract's slot to another vendor.</summary>
    [HttpPost]
    public async Task<ActionResult<SlotTransferRequestDto>> Submit(
        RequestTransferRequest request, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new RequestTransferCommand(request.ContractId, request.ToVendorPhone), cancellationToken);
        return Ok(new { message = SideMessages.TransferRequested, data = result });
    }

    /// <summary>SIDE-12/13: list the caller's own transfer requests, sent (outgoing) or received (incoming).</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<SlotTransferRequestDto>>> List(
        [FromQuery] string direction, CancellationToken cancellationToken)
        => Ok(await sender.Send(new ListTransfersQuery(direction), cancellationToken));

    /// <summary>SIDE-13: the receiving vendor accepts a pending transfer.</summary>
    [HttpPost("{transferId:long}/accept")]
    public async Task<IActionResult> Accept(long transferId, CancellationToken cancellationToken)
    {
        await sender.Send(new AcceptTransferCommand(transferId), cancellationToken);
        return Ok(new { message = SideMessages.TransferAccepted });
    }

    /// <summary>SIDE-13: the receiving vendor declines a pending transfer.</summary>
    [HttpPost("{transferId:long}/decline")]
    public async Task<IActionResult> Decline(long transferId, CancellationToken cancellationToken)
    {
        await sender.Send(new DeclineTransferCommand(transferId), cancellationToken);
        return Ok(new { message = SideMessages.TransferDeclined });
    }
}
