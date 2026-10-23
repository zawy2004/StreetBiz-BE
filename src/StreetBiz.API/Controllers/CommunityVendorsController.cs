using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Net.Http.Headers;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Community;
using StreetBiz.Application.Features.CommunityVendors;

namespace StreetBiz.API.Controllers;

[ApiController]
[Route("api/community")]
public sealed class CommunityVendorsController(ISender sender) : ControllerBase
{
    /// <summary>BUY-01: active, currently permitted vendors for the customer map.</summary>
    [AllowAnonymous]
    [HttpGet("vendors")]
    public async Task<ActionResult<IReadOnlyList<ActiveVendorLocationDto>>> Search(
        [FromQuery] decimal? latitude,
        [FromQuery] decimal? longitude,
        [FromQuery] double? radiusMeters,
        [FromQuery] int take = 100,
        CancellationToken cancellationToken = default) =>
        Ok(await sender.Send(
            new SearchActiveVendorsQuery(latitude, longitude, radiusMeters, take),
            cancellationToken));

    /// <summary>
    /// BUY-02 / BUY-06: cryptographically verify a permit QR (the bare token or a /q/{token} link)
    /// and its live database status, with the stall's price board when the permit is VALID.
    /// BR-39: a conclusion is never to be reused, so the response forbids every cache.
    /// </summary>
    [AllowAnonymous]
    [EnableRateLimiting("PublicPermitCheck")]
    [HttpPost("permits/verify")]
    public async Task<ActionResult<PermitVerificationDto>> VerifyPermit(
        VerifyPermitRequest request,
        CancellationToken cancellationToken)
    {
        Response.GetTypedHeaders().CacheControl = new CacheControlHeaderValue { NoStore = true };
        return Ok(await sender.Send(new VerifyPublicPermitCommand(
            request.QrPayload,
            request.Latitude,
            request.Longitude,
            request.PhotoUrl,
            request.PrintedVersion), cancellationToken));
    }

    /// <summary>BUY-03: public identity, permit, location, ratings and comments.</summary>
    [AllowAnonymous]
    [HttpGet("vendors/{vendorId:long}")]
    public async Task<ActionResult<PublicVendorProfileDto>> Profile(
        long vendorId,
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(new GetPublicVendorProfileQuery(vendorId), cancellationToken));

    /// <summary>BUY-04: create or replace the current customer's vendor rating/comment.</summary>
    [Authorize]
    [HttpPut("vendors/{vendorId:long}/comment")]
    public async Task<ActionResult<VendorCommentDto>> Comment(
        long vendorId,
        UpsertVendorCommentRequest request,
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(new UpsertVendorCommentCommand(
            vendorId,
            request.Rating,
            request.CommentText), cancellationToken));

    /// <summary>
    /// BUY-05 / BUY-07: report a vendor to the responsible ward. An OVERCHARGE names the dish, the
    /// quantity, the amount charged and when; the posted price is the server's, from the price history
    /// at that moment. 409 while the caller already has a pending report about this vendor.
    /// </summary>
    [Authorize]
    [EnableRateLimiting("CommunityReport")]
    [HttpPost("vendors/{vendorId:long}/reports")]
    public async Task<ActionResult<VendorReportReceiptDto>> Report(
        long vendorId,
        ReportSuspiciousVendorRequest request,
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(new ReportSuspiciousVendorCommand(
            vendorId,
            request.Reason,
            request.EvidenceUrl,
            request.SlotId,
            request.ScannedPermitId,
            request.ReportType ?? VendorReportTypes.General,
            request.MenuItemId,
            request.Quantity,
            request.ChargedAmount,
            request.PurchasedAt), cancellationToken));

    /// <summary>"Phản ánh của tôi": the calling customer's reports with their status, due time and outcome.</summary>
    [Authorize]
    [HttpGet("my-reports")]
    public async Task<ActionResult<IReadOnlyList<MyVendorReportDto>>> MyReports(
        [FromQuery] int page,
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(new ListMyVendorReportsQuery(page < 1 ? 1 : page), cancellationToken));
}

public sealed record VerifyPermitRequest(
    string QrPayload,
    decimal? Latitude,
    decimal? Longitude,
    string? PhotoUrl,
    int? PrintedVersion = null);

public sealed record UpsertVendorCommentRequest(
    short Rating,
    string CommentText);

/// <summary>
/// There is deliberately no posted-price field: the posted price of an OVERCHARGE is computed by the
/// server from MenuItemPriceHistory at <see cref="PurchasedAt"/>, and anything else a client sends is
/// ignored.
/// </summary>
public sealed record ReportSuspiciousVendorRequest(
    string? Reason,
    string? EvidenceUrl,
    long? SlotId,
    long? ScannedPermitId,
    string? ReportType = null,
    long? MenuItemId = null,
    int? Quantity = null,
    decimal? ChargedAmount = null,
    DateTimeOffset? PurchasedAt = null);
