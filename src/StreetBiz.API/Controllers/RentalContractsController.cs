using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StreetBiz.Application.DTOs.RentalContracts;
using StreetBiz.Application.Features.RentalContracts.GetContract;
using StreetBiz.Application.Features.RentalContracts.ListContracts;

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
}
