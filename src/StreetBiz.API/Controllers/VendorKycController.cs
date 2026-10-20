using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using StreetBiz.Application.Features.VendorKyc;

namespace StreetBiz.API.Controllers;

/// <summary>
/// REG-02 eKYC helpers used while the registration wizard is being filled in: read the
/// applicant's CCCD to pre-fill the Mẫu số 01 fields, and compare their portrait against the
/// photo on that card.
///
/// Both routes spend real FPT.AI credits per call, so both are [Authorize]d, vendor-only
/// (enforced in the handlers via IVendorContext) and rate limited per account.
/// </summary>
[ApiController]
[Authorize]
[Route("api/vendor/kyc")]
[EnableRateLimiting("VendorKycAi")]
public sealed class VendorKycController(ISender sender) : ControllerBase
{
    /// <summary>Reads both sides of a CCCD. Dân tộc / ngày cấp / nơi cấp need the back photo.</summary>
    [HttpPost("id-card")]
    public async Task<ActionResult<KycIdCardExtraction>> ExtractIdCard(
        [FromBody] ExtractKycIdCardCommand request,
        CancellationToken ct) =>
        Ok(await sender.Send(request, ct));

    /// <summary>Portrait ↔ CCCD photo comparison. Decision support only (BR-41).</summary>
    [HttpPost("face-match")]
    public async Task<ActionResult<KycFaceMatchResult>> MatchFace(
        [FromBody] MatchKycFaceCommand request,
        CancellationToken ct) =>
        Ok(await sender.Send(request, ct));
}
