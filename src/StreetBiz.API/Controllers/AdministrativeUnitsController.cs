using MediatR;
using Microsoft.AspNetCore.Mvc;
using StreetBiz.Application.DTOs.AdministrativeUnits;
using StreetBiz.Application.Features.AdministrativeUnits.ListWards;

namespace StreetBiz.API.Controllers;

[ApiController]
[Route("api/administrative-units")]
public sealed class AdministrativeUnitsController(ISender sender) : ControllerBase
{
    /// <summary>
    /// Wards for the sign-up and business-registration pickers. Anonymous because
    /// a guest has to choose a ward before an account exists.
    /// </summary>
    [HttpGet("wards")]
    public async Task<ActionResult<IReadOnlyList<WardDto>>> Wards(CancellationToken cancellationToken)
        => Ok(await sender.Send(new ListWardsQuery(), cancellationToken));
}
