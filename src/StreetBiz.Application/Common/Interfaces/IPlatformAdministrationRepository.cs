using StreetBiz.Application.Common.Models;

namespace StreetBiz.Application.Common.Interfaces;

public interface IPlatformAdministrationRepository
{
    Task<IReadOnlyList<FoodCategoryRow>> ListFoodCategoriesAsync(CancellationToken cancellationToken);

    Task<bool> FoodCategoryNameExistsAsync(
        string name,
        int? exceptCategoryId,
        CancellationToken cancellationToken);

    Task<FoodCategoryRow?> CreateFoodCategoryAsync(
        string name,
        long actorUserId,
        CancellationToken cancellationToken);

    Task<FoodCategoryRow?> RenameFoodCategoryAsync(
        int categoryId,
        string name,
        long actorUserId,
        CancellationToken cancellationToken);

    Task<CategoryDeleteOutcome> DeleteFoodCategoryAsync(
        int categoryId,
        long actorUserId,
        CancellationToken cancellationToken);

    Task<PlatformPage<ReportedContentRow>> ListReportedContentAsync(
        string? status,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<ReportedContentRow?> GetReportedContentAsync(
        long reportId,
        CancellationToken cancellationToken);

    Task<ContentDecisionResult> DecideReportedContentAsync(
        long reportId,
        string expectedStatus,
        string decision,
        long actorUserId,
        CancellationToken cancellationToken);

    /// <summary>Undoes a HIDE: the report that hid the content (and any duplicate hidden with it)
    /// is closed as DISMISSED and the content is handed back to its owner.</summary>
    Task<ContentDecisionResult> RestoreReportedContentAsync(
        long reportId,
        string expectedStatus,
        long actorUserId,
        CancellationToken cancellationToken);

    Task<PlatformPage<OrderComplaintRow>> ListOrderComplaintsAsync(
        string? status,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<OrderComplaintRow?> GetOrderComplaintAsync(
        long complaintId,
        CancellationToken cancellationToken);

    Task<ComplaintDecisionResult> DecideOrderComplaintAsync(
        long complaintId,
        ComplaintResolution resolution,
        long actorUserId,
        CancellationToken cancellationToken);

    /// <summary>ADM-02: Customer and Vendor accounts only; officers and admins are never listed.</summary>
    Task<PlatformPage<PlatformAccountRow>> ListAccountsAsync(
        string? role,
        string? status,
        string? search,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<PlatformAccountDetailRow?> GetAccountAsync(long userId, CancellationToken cancellationToken);

    /// <summary>
    /// ADM-02: moves a Customer/Vendor account between ACTIVE and SUSPENDED in one serializable
    /// transaction together with the audit row and the notification. Suspending also revokes every
    /// session and pauses the vendor's open storefronts; reactivating reopens nothing.
    /// </summary>
    /// <summary>ADM-06: every figure for the period and the one before it, counted in a handful of grouped queries.</summary>
    Task<PlatformDashboardFacts> GetDashboardFactsAsync(
        DashboardRange range,
        CancellationToken cancellationToken);

    Task<AccountStatusChangeResult> ChangeAccountStatusAsync(
        long userId,
        string targetStatus,
        string expectedStatus,
        string? reason,
        long actorUserId,
        CancellationToken cancellationToken);
}
