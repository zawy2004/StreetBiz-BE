using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StreetBiz.Application.Features.RegistrationIntegrity;

namespace StreetBiz.API.Controllers;

/// <summary>
/// AIC-03: a warning, for a ward officer reviewing a registration, that the same CCCD or address
/// was filed through another account. Advisory only (BR-41); role and ward scoping happen inside
/// the handler via IWardActorContext.
/// </summary>
[ApiController]
[Authorize]
[Route("api/ward/integrity")]
public sealed class WardIntegrityController(ISender sender) : ControllerBase
{
    /// <summary>AIC-03: duplicate / suspicious-application findings for one registration of the officer's ward.</summary>
    [HttpGet("registrations/{registrationId:long}")]
    public async Task<ActionResult<RegistrationIntegrityDto>> Registration(
        long registrationId,
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(new GetRegistrationIntegrityQuery(registrationId), cancellationToken));
}
