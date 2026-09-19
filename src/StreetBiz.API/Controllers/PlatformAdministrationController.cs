using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StreetBiz.Application.DTOs.PlatformAdministration;
using StreetBiz.Application.Features.PlatformAdministration;

namespace StreetBiz.API.Controllers;

[ApiController]
[Authorize]
[Route("api/platform")]
public sealed class PlatformAdministrationController(ISender sender) : ControllerBase
{
    [HttpGet("me")]
    public async Task<ActionResult<PlatformAdminProfileDto>> Profile(
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(new GetPlatformAdminProfileQuery(), cancellationToken));

    /// <summary>ADM-01: list food categories and their current menu-item usage.</summary>
    [HttpGet("food-categories")]
    public async Task<ActionResult<IReadOnlyList<FoodCategoryDto>>> Categories(
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(new ListFoodCategoriesQuery(), cancellationToken));

    /// <summary>ADM-01: create a unique food category.</summary>
    [HttpPost("food-categories")]
    public async Task<ActionResult<FoodCategoryDto>> CreateCategory(
        FoodCategoryRequest request,
        CancellationToken cancellationToken)
    {
        var created = await sender.Send(new CreateFoodCategoryCommand(request.Name), cancellationToken);
        return CreatedAtAction(nameof(Categories), created);
    }

    /// <summary>ADM-01: rename a food category.</summary>
    [HttpPut("food-categories/{categoryId:int}")]
    public async Task<ActionResult<FoodCategoryDto>> RenameCategory(
        int categoryId,
        FoodCategoryRequest request,
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(
            new RenameFoodCategoryCommand(categoryId, request.Name), cancellationToken));

    /// <summary>ADM-01: delete an unused food category.</summary>
    [HttpDelete("food-categories/{categoryId:int}")]
    public async Task<IActionResult> DeleteCategory(
        int categoryId,
        CancellationToken cancellationToken)
    {
        await sender.Send(new DeleteFoodCategoryCommand(categoryId), cancellationToken);
        return NoContent();
    }

    /// <summary>ADM-03: list the reported-content review queue.</summary>
    [HttpGet("reported-content")]
    public async Task<ActionResult<PlatformPageDto<ReportedContentDto>>> ReportedContent(
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default) =>
        Ok(await sender.Send(
            new ListReportedContentQuery(status, page, pageSize), cancellationToken));

    /// <summary>ADM-03: view a reported content record and its current content snapshot.</summary>
    [HttpGet("reported-content/{reportId:long}")]
    public async Task<ActionResult<ReportedContentDto>> ReportedContentDetail(
        long reportId,
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(new GetReportedContentQuery(reportId), cancellationToken));

    /// <summary>ADM-03: dismiss a report that does not establish a violation.</summary>
    [HttpPost("reported-content/{reportId:long}/dismiss")]
    public async Task<ActionResult<ReportedContentDto>> DismissReportedContent(
        long reportId,
        ExpectedStatusRequest request,
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(new DecideReportedContentCommand(
            reportId,
            "DISMISS",
            request.ExpectedStatus), cancellationToken));

    /// <summary>ADM-04: soft-hide violating content and close duplicate pending reports.</summary>
    [HttpPost("reported-content/{reportId:long}/hide")]
    public async Task<ActionResult<ReportedContentDto>> HideReportedContent(
        long reportId,
        ExpectedStatusRequest request,
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(new DecideReportedContentCommand(
            reportId,
            "HIDE",
            request.ExpectedStatus), cancellationToken));

    /// <summary>ADM-05: list order complaints.</summary>
    [HttpGet("order-complaints")]
    public async Task<ActionResult<PlatformPageDto<OrderComplaintDto>>> OrderComplaints(
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default) =>
        Ok(await sender.Send(
            new ListOrderComplaintsQuery(status, page, pageSize), cancellationToken));

    /// <summary>ADM-05: view an order complaint and payment/refund context.</summary>
    [HttpGet("order-complaints/{complaintId:long}")]
    public async Task<ActionResult<OrderComplaintDto>> OrderComplaintDetail(
        long complaintId,
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(new GetOrderComplaintQuery(complaintId), cancellationToken));

    /// <summary>ADM-05: resolve or reject a complaint, optionally requesting a refund.</summary>
    [HttpPost("order-complaints/{complaintId:long}/decision")]
    public async Task<ActionResult<OrderComplaintDto>> DecideOrderComplaint(
        long complaintId,
        OrderComplaintDecisionRequest request,
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(new DecideOrderComplaintCommand(
            complaintId,
            request.Decision,
            request.Notes,
            request.ExpectedStatus,
            request.ApprovedRefundAmount), cancellationToken));
}

public sealed record FoodCategoryRequest(string Name);

public sealed record ExpectedStatusRequest(string ExpectedStatus);

public sealed record OrderComplaintDecisionRequest(
    string Decision,
    string Notes,
    string ExpectedStatus,
    decimal? ApprovedRefundAmount);
