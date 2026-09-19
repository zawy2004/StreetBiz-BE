using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
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

    /// <summary>BUY-02: cryptographically verify a permit QR and its live database status.</summary>
    [AllowAnonymous]
    [EnableRateLimiting("PublicPermitCheck")]
    [HttpPost("permits/verify")]
    public async Task<ActionResult<PermitVerificationDto>> VerifyPermit(
        VerifyPermitRequest request,
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(new VerifyPublicPermitCommand(
            request.QrPayload,
            request.Latitude,
            request.Longitude,
            request.PhotoUrl), cancellationToken));

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

    /// <summary>BUY-05: report a suspicious vendor to the responsible ward workflow.</summary>
    [Authorize]
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
            request.ScannedPermitId), cancellationToken));
}

public sealed record VerifyPermitRequest(
    string QrPayload,
    decimal? Latitude,
    decimal? Longitude,
    string? PhotoUrl);

public sealed record UpsertVendorCommentRequest(
    short Rating,
    string CommentText);

public sealed record ReportSuspiciousVendorRequest(
    string Reason,
    string? EvidenceUrl,
    long? SlotId,
    long? ScannedPermitId);
