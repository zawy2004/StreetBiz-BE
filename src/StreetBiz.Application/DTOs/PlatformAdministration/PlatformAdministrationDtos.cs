namespace StreetBiz.Application.DTOs.PlatformAdministration;

public sealed record PlatformAdminProfileDto(long UserId, string Name);

public sealed record PlatformPageDto<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount);

public sealed record FoodCategoryDto(
    int CategoryId,
    string CategoryName,
    int ItemCount,
    string? CreatedByName);

public sealed record ReportedContentDto(
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

public sealed record OrderComplaintDto(
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
