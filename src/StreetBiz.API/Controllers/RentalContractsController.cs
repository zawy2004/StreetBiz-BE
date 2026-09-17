using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.DigitalPermits;
using StreetBiz.Application.DTOs.RentalContracts;
using StreetBiz.Application.Features.DigitalPermits.GetPermit;
using StreetBiz.Application.Features.RentalContracts.CancelContract;
using StreetBiz.Application.Features.RentalContracts.GetContract;
using StreetBiz.Application.Features.RentalContracts.ListContracts;
using StreetBiz.Application.Features.RentalContracts.ListRenewals;
using StreetBiz.Application.Features.RentalContracts.RequestRenewal;

namespace StreetBiz.API.Controllers;

[ApiController]
[Authorize]
[Route("api/vendor/rental-contracts")]
public sealed class RentalContractsController(ISender sender) : ControllerBase
{
    /// <summary>SIDE-05: list the caller's rental contracts, optionally filtered by status.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<RentalContractDto>>> List(
        [FromQuery] string? status, CancellationToken cancellationToken)
        => Ok(await sender.Send(new ListContractsQuery(status), cancellationToken));

    /// <summary>SIDE-05: one rental contract's detail.</summary>
    [HttpGet("{contractId:long}")]
    public async Task<ActionResult<RentalContractDto>> Get(long contractId, CancellationToken cancellationToken)
        => Ok(await sender.Send(new GetContractQuery(contractId), cancellationToken));

    /// <summary>SIDE-06: request a renewal of an active contract.</summary>
    [HttpPost("{contractId:long}/renewals")]
    public async Task<ActionResult<RenewalRequestDto>> RequestRenewal(
        long contractId, RequestRenewalRequest request, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new RequestRenewalCommand(contractId, request.RequestedTermDays), cancellationToken);
        return Ok(new { message = SideMessages.RenewalRequested, data = result });
    }

    /// <summary>SIDE-06: track the renewal requests raised for this contract.</summary>
    [HttpGet("{contractId:long}/renewals")]
    public async Task<ActionResult<IReadOnlyList<RenewalRequestDto>>> ListRenewals(
        long contractId, CancellationToken cancellationToken)
        => Ok(await sender.Send(new ListRenewalsQuery(contractId), cancellationToken));

    /// <summary>SIDE-07: voluntarily return the slot. No ward approval is involved.</summary>
    [HttpPost("{contractId:long}/cancel")]
    public async Task<IActionResult> Cancel(long contractId, CancelContractRequest request, CancellationToken cancellationToken)
    {
        await sender.Send(new CancelContractCommand(contractId, request.Reason), cancellationToken);
        return Ok(new { message = SideMessages.ContractCancelled });
    }

    /// <summary>SIDE-08: view the digital QR permit for this contract.</summary>
    [HttpGet("{contractId:long}/permit")]
    public async Task<ActionResult<DigitalPermitDto>> GetPermit(long contractId, CancellationToken cancellationToken)
        => Ok(await sender.Send(new GetPermitQuery(contractId), cancellationToken));
}
