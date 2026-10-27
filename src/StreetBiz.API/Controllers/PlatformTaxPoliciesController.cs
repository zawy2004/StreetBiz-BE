using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StreetBiz.Application.Features.Tax;

namespace StreetBiz.API.Controllers;

/// <summary>
/// The parameters the household tax module runs on (thresholds, rates, the notice date). Platform
/// administrators publish a new version of a year's rules; a published version is never edited, and
/// the highest version of a year is the one in force.
/// </summary>
[ApiController]
[Authorize]
[Route("api/platform/tax-policies")]
public sealed class PlatformTaxPoliciesController(ISender sender) : ControllerBase
{
    /// <summary>Every published version, newest first, with the one in force for each year marked.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<TaxPolicyVersionDto>>> List(CancellationToken cancellationToken) =>
        Ok(await sender.Send(new ListTaxPoliciesQuery(), cancellationToken));

    /// <summary>Publishes a new version of a tax year's rules.</summary>
    [HttpPost]
    public async Task<ActionResult<TaxPolicyVersionDto>> Create(
        CreateTaxPolicyRequest request, CancellationToken cancellationToken) =>
        Ok(await sender.Send(new CreateTaxPolicyCommand(request), cancellationToken));
}
