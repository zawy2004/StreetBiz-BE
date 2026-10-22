using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using StreetBiz.API.Extensions;
using StreetBiz.Application.Features.AiMarketplace;

namespace StreetBiz.API.Controllers;

public sealed record DishDescriptionRequest(string Name, string? ImageUrl);

public sealed record DishRecognitionRequest(string ImageUrl);

public sealed record SearchIntentRequest(string Text);

/// <summary>
/// AI suggestions for sellers and customers (AIP-01..04). Every answer is a suggestion for a person to accept or
/// ignore (BR-41); nothing here saves, orders or publishes anything. A model that is off or down is a normal
/// answer (<c>available: false</c>), never an error.
/// </summary>
[ApiController]
[Route("api/ai")]
[EnableRateLimiting(AiRateLimitSetup.Policy)]
public sealed class AiAssistController(ISender sender) : ControllerBase
{
    /// <summary>AIP-01: a draft description for a dish, from its name and optionally its photo. Vendors only.</summary>
    [HttpPost("dish-description")]
    [Authorize]
    public async Task<ActionResult<DishDescriptionSuggestionDto>> DishDescription(
        DishDescriptionRequest request,
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(new GenerateDishDescriptionCommand(request.Name, request.ImageUrl), cancellationToken));

    /// <summary>AIP-02: a dish name, a platform category and a reference price from the vendor's own dish photo.</summary>
    [HttpPost("dish-recognition")]
    [Authorize]
    public async Task<ActionResult<DishRecognitionDto>> DishRecognition(
        DishRecognitionRequest request,
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(new RecognizeDishImageCommand(request.ImageUrl), cancellationToken));

    /// <summary>AIP-03: turns a sentence ("bún chả dưới 50k gần đây") into the marketplace's own filters.</summary>
    [HttpPost("search-intent")]
    [AllowAnonymous]
    public async Task<ActionResult<SearchIntentDto>> SearchIntent(
        SearchIntentRequest request,
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(new InterpretSearchQuery(request.Text), cancellationToken));

    /// <summary>AIP-04: what reviewers keep saying about one public storefront (needs enough written reviews).</summary>
    [HttpGet("storefronts/{storefrontId:long}/review-summary")]
    [AllowAnonymous]
    public async Task<ActionResult<ReviewSummaryDto>> ReviewSummary(
        long storefrontId,
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(new SummarizeStorefrontReviewsQuery(storefrontId), cancellationToken));
}

/// <summary>
/// AIB-01: the ward officer's view of citizen reports about vendors, with the system's notes beside each one
/// (the signage and place read off the photo, and a check of the scanned permit). The officer decides; the notes
/// never accept, reject or close a report. Role and ward scoping happen inside the handlers.
/// </summary>
[ApiController]
[Authorize]
[Route("api/ward/vendor-reports")]
public sealed class WardVendorReportsController(ISender sender) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<WardVendorReportSummaryDto>>> List(
        [FromQuery] string? status,
        [FromQuery] int page,
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(new ListWardVendorReportsQuery(status, page < 1 ? 1 : page), cancellationToken));

    [HttpGet("{reportId:long}")]
    public async Task<ActionResult<WardVendorReportDto>> Get(long reportId, CancellationToken cancellationToken) =>
        Ok(await sender.Send(new GetWardVendorReportQuery(reportId), cancellationToken));

    /// <summary>Reads the photo again and returns the refreshed notes. Rate limited because it calls the model.</summary>
    [HttpPost("{reportId:long}/assist")]
    [EnableRateLimiting(AiRateLimitSetup.Policy)]
    public async Task<ActionResult<WardVendorReportDto>> Assist(long reportId, CancellationToken cancellationToken) =>
        Ok(await sender.Send(new RunVendorReportAssistCommand(reportId), cancellationToken));
}
