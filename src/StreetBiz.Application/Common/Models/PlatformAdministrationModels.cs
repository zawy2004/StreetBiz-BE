namespace StreetBiz.Application.Common.Models;

public sealed record PlatformAdminActor(long UserId, string Name);

public sealed record PlatformPage<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount);

public sealed record FoodCategoryRow(
    int CategoryId,
    string CategoryName,
    int ItemCount,
    string? CreatedByName);

public sealed record ReportedContentRow(
    long ReportId,
    string ContentType,
    long ContentId,
    string ContentTitle,
    string? ContentBody,
    string ContentStatus,
    bool ContentExists,
    string ReporterName,
    string Reason,
    string Status,
    string? ReviewedByName,
    DateTime CreatedAt,
    DateTime? ReviewedAt);

public sealed record OrderComplaintRow(
    long ComplaintId,
    long OrderId,
    string OrderCode,
    string OrderStatus,
    string CustomerName,
    string StorefrontName,
    string ComplaintType,
    string Description,
    decimal? RequestedRefundAmount,
    string Status,
    string? ResolutionNotes,
    string? ResolvedByName,
    DateTime CreatedAt,
    DateTime? ResolvedAt,
    decimal? PaymentAmount,
    string? PaymentProvider,
    decimal RefundedAmount,
    long? LatestRefundId,
    string? LatestRefundStatus);

public enum CategoryDeleteOutcome
{
    Deleted,
    NotFound,
    InUse,
}

public enum ContentDecisionOutcome
{
    Updated,
    NotFound,
    Conflict,
    ContentNotFound,
}

public sealed record ContentDecisionResult(
    ContentDecisionOutcome Outcome,
    ReportedContentRow? Report);

public sealed record ComplaintResolution(
    string Decision,
    string Notes,
    string ExpectedStatus,
    decimal? ApprovedRefundAmount);

public enum ComplaintDecisionOutcome
{
    Updated,
    NotFound,
    Conflict,
    RefundNotAllowed,
    PaymentNotFound,
    RefundExceedsLimit,
}

public sealed record ComplaintDecisionResult(
    ComplaintDecisionOutcome Outcome,
    OrderComplaintRow? Complaint);
