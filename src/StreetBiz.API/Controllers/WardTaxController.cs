using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StreetBiz.Application.Features.Tax;

namespace StreetBiz.API.Controllers;

/// <summary>
/// The ward's side of household tax: the list of households it keeps current for the tax office,
/// what to pass on to that office, announcements to households, and the Excel hand-over. The ward
/// collects no tax and sees a household's revenue only when that household agreed. Every route is
/// scoped to the officer's own ward.
/// </summary>
[ApiController]
[Authorize]
[Route("api/ward/tax")]
public sealed class WardTaxController(ISender sender) : ControllerBase
{
    /// <summary>Counts only: households by status, tax code, notice and revenue group; no household is named.</summary>
    [HttpGet("dashboard")]
    public async Task<ActionResult<WardTaxDashboardDto>> Dashboard([FromQuery] int? year, CancellationToken cancellationToken) =>
        Ok(await sender.Send(new GetWardTaxDashboardQuery(year), cancellationToken));

    /// <summary>The households of the ward. Revenue and group are present only for households that share them.</summary>
    [HttpGet("households")]
    public async Task<ActionResult<WardTaxHouseholdsDto>> Households([FromQuery] int? year, CancellationToken cancellationToken) =>
        Ok(await sender.Send(new ListWardTaxHouseholdsQuery(year), cancellationToken));

    /// <summary>Cases to pass on to the tax office, pending first.</summary>
    [HttpGet("referrals")]
    public async Task<ActionResult<IReadOnlyList<TaxReferralDto>>> Referrals(
        [FromQuery] string? status, CancellationToken cancellationToken) =>
        Ok(await sender.Send(new ListTaxReferralsQuery(status), cancellationToken));

    /// <summary>Looks for new cases in the ward's own records (violations, scans, address changes, ended contracts, new households).</summary>
    [HttpPost("referrals/refresh")]
    public async Task<ActionResult<TaxReferralRefreshDto>> RefreshReferrals(CancellationToken cancellationToken) =>
        Ok(await sender.Send(new RefreshTaxReferralsCommand(), cancellationToken));

    /// <summary>Records that the case was passed on to the tax office (optionally with the letter's reference).</summary>
    [HttpPost("referrals/{referralId:long}/report")]
    public async Task<ActionResult<TaxReferralDto>> ReportReferral(
        long referralId, ReportTaxReferralRequest request, CancellationToken cancellationToken) =>
        Ok(await sender.Send(new ReportTaxReferralCommand(referralId, request), cancellationToken));

    /// <summary>Records that the case needs no report, with the reason.</summary>
    [HttpPost("referrals/{referralId:long}/dismiss")]
    public async Task<ActionResult<TaxReferralDto>> DismissReferral(
        long referralId, DismissTaxReferralRequest request, CancellationToken cancellationToken) =>
        Ok(await sender.Send(new DismissTaxReferralCommand(referralId, request), cancellationToken));

    /// <summary>Messages the ward has sent to its households about tax.</summary>
    [HttpGet("announcements")]
    public async Task<ActionResult<IReadOnlyList<TaxAnnouncementDto>>> Announcements(CancellationToken cancellationToken) =>
        Ok(await sender.Send(new ListTaxAnnouncementsQuery(), cancellationToken));

    /// <summary>Sends a tax message to a group of the ward's households (everyone, those who have not filed, those without a tax code).</summary>
    [HttpPost("announcements")]
    public async Task<ActionResult<TaxAnnouncementDto>> SendAnnouncement(
        SendTaxAnnouncementRequest request, CancellationToken cancellationToken) =>
        Ok(await sender.Send(new SendTaxAnnouncementCommand(request), cancellationToken));

    /// <summary>The household list and the cases for the tax office as an .xlsx workbook.</summary>
    [HttpGet("export")]
    public async Task<IActionResult> Export([FromQuery] int? year, CancellationToken cancellationToken)
    {
        var file = await sender.Send(new ExportWardTaxQuery(year), cancellationToken);
        return File(file.Content, file.ContentType, file.FileName);
    }
}
