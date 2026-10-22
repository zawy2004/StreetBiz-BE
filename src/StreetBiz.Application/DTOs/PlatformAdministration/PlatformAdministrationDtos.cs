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

public sealed record PlatformAccountDto(
    long UserId,
    string? FullName,
    string MaskedPhone,
    string RoleCode,
    string AccountStatus,
    DateTime CreatedAt,
    DateTime? LastLoginAt,
    int ActiveSessions,
    int StorefrontCount);

public sealed record PlatformAccountEventDto(
    string Action,
    string ActorName,
    string? Reason,
    DateTime CreatedAt);

public sealed record PlatformAccountDetailDto(
    PlatformAccountDto Account,
    IReadOnlyList<PlatformAccountEventDto> History);

/// <summary>A figure with the previous period beside it. ChangePercent is null when there is nothing to compare with.</summary>
public sealed record DashboardMetricDto(decimal Value, decimal Previous, decimal? ChangePercent);

public sealed record DashboardUsersDto(
    DashboardMetricDto NewUsers,
    int Total,
    int Customers,
    int Vendors,
    int WardAuthorities,
    int PlatformAdmins,
    int Suspended);

public sealed record DashboardMarketplaceDto(
    int VendorAccounts,
    int StorefrontsOpen,
    int StorefrontsPaused,
    int StorefrontsClosed,
    int StorefrontsHidden);

public sealed record DashboardOrderStatusDto(string Status, int Count);

public sealed record DashboardOrdersDto(
    DashboardMetricDto Placed,
    DashboardMetricDto Completed,
    DashboardMetricDto Gmv,
    // Cancelled plus rejected orders as a share of the orders placed, in percent.
    DashboardMetricDto CancelRejectRate,
    IReadOnlyList<DashboardOrderStatusDto> ByStatus);

public sealed record DashboardModerationDto(
    int PendingReports,
    DashboardMetricDto ReportsFiled,
    double? AverageReviewHours,
    int OpenComplaints,
    int PendingRefunds,
    decimal PendingRefundAmount);

public sealed record DashboardDayDto(DateOnly Day, int OrdersPlaced, int OrdersCompleted, decimal Gmv);

public sealed record DashboardTopStorefrontDto(
    long StorefrontId,
    string StorefrontName,
    int CompletedOrders,
    decimal Revenue);

public sealed record PlatformDashboardDto(
    DateOnly From,
    DateOnly To,
    DateOnly PreviousFrom,
    DateOnly PreviousTo,
    DashboardUsersDto Users,
    DashboardMarketplaceDto Marketplace,
    DashboardOrdersDto Orders,
    DashboardModerationDto Moderation,
    IReadOnlyList<DashboardDayDto> OrdersByDay,
    IReadOnlyList<DashboardTopStorefrontDto> TopStorefronts);

public sealed record PlatformExportDto(
    long ExportId,
    string ReportType,
    DateOnly? From,
    DateOnly? To,
    string Format,
    string Status,
    string? FailureReason,
    string? FileName,
    DateTime RequestedAt,
    DateTime? CompletedAt);
