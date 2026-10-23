
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.DTOs.Community;

namespace StreetBiz.Application.Features.AiMarketplace;

#region Read models

public sealed record AiCategoryRow(int CategoryId, string CategoryName);

/// <summary>What the platform's own dishes in one category cost, so a vendor can price a new dish (system-computed, not AI).</summary>
public sealed record CategoryPriceRow(int DishCount, decimal Lowest, decimal Median, decimal Highest);

public sealed record ReviewForSummaryRow(long ReviewId, short Rating, string Text, DateTime CreatedAt, DateTime? EditedAt);

public sealed record StorefrontReviewsRow(string StorefrontName, IReadOnlyList<ReviewForSummaryRow> Reviews, int TotalReviews);

/// <summary>Everything the report assistant and the ward's view need about one citizen report.</summary>
public sealed record VendorReportContextRow(
    long ReportId,
    string Reason,
    string? EvidenceUrl,
    string Status,
    DateTime CreatedAt,
    string? AiExtractedLocation,
    string? VendorName,
    long? VendorId,
    long? SlotId,
    string? SlotCode,
    double? SlotLatitude,
    double? SlotLongitude,
    long? ScannedPermitId,
    string? PermitStatus,
    string? PermitContractStatus,
    string? PermitSlotCode,
    long? PermitSlotId,
    IReadOnlyList<int> WardIds,
    string ReportType = "GENERAL",
    DateTime? DueAt = null,
    VendorReportOutcomeRow? Outcome = null,
    VendorReportPriceEvidenceRow? PriceEvidence = null,
    int ReportsAgainstVendor90Days = 0,
    long? ViolationId = null);

/// <summary>How an officer closed a report (WARD-19); null while it is pending.</summary>
public sealed record VendorReportOutcomeRow(
    string Resolution,
    string? Note,
    decimal? RefundAmount,
    DateTime? ReviewedAt,
    string? ReviewedByName);

/// <param name="Overdue">true: only pending reports past their due time; false: all others.</param>
public sealed record VendorReportListFilter(
    string? Status,
    string? Type,
    bool? Overdue,
    DateTime NowUtc);

public interface IAiMarketplaceRepository
{
    Task<IReadOnlyList<AiCategoryRow>> ListCategoriesAsync(CancellationToken cancellationToken);

    /// <summary>Null when fewer than three dishes in the category are on sale (too few to say what is usual).</summary>
    Task<CategoryPriceRow?> GetCategoryPriceAsync(int categoryId, CancellationToken cancellationToken);

    /// <summary>Null when the storefront is not publicly listed. Hidden reviews are never included.</summary>
    Task<StorefrontReviewsRow?> GetReviewsForSummaryAsync(long storefrontId, int take, CancellationToken cancellationToken);

    Task<VendorReportContextRow?> GetVendorReportAsync(long reportId, CancellationToken cancellationToken);

    /// <summary>
    /// The ward's reports, pending and overdue first, then pending by nearest due time, then the
    /// closed ones newest first.
    /// </summary>
    Task<IReadOnlyList<VendorReportContextRow>> ListVendorReportsForWardAsync(
        int wardId, VendorReportListFilter filter, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>True when a vendor report of the officer's ward carries this evidence file (so that officer may open it).</summary>
    Task<bool> ReportEvidenceBelongsToWardAsync(string evidenceUrl, int wardId, CancellationToken cancellationToken);

    /// <summary>Stores the AIB-01 location text on the report (VendorReports.ai_extracted_location, 200 characters).</summary>
    Task SetReportLocationAsync(long reportId, string location, CancellationToken cancellationToken);
}

#endregion

#region AIP-01 / AIP-02 (vendor)

public sealed record DishDescriptionSuggestionDto(
    bool Available,
    string? Description,
    string Message,
    long? AiLogId);

public sealed record ReferencePriceDto(int DishCount, decimal Lowest, decimal Median, decimal Highest);

public sealed record DishRecognitionDto(
    bool Available,
    bool Recognized,
    string? DishName,
    int? CategoryId,
    string? CategoryName,
    int? Confidence,
    ReferencePriceDto? ReferencePrice,
    string Message,
    long? AiLogId);

#endregion

#region AIP-03 (customer / guest)

public sealed record SearchIntentDto(
    bool Available,
    // "AI" when the model read the sentence, "RULES" when the built-in keyword rules did (the model was off or down).
    string Source,
    string? Query,
    int? CategoryId,
    string? CategoryName,
    decimal? MinPrice,
    decimal? MaxPrice,
    bool? OpenNow,
    decimal? MinRating,
    bool NearMe,
    int? RadiusMeters,
    string? Sort,
    string Explanation);

#endregion

#region AIP-04

public sealed record ReviewSummaryDto(
    bool Available,
    // NOT_ENOUGH_REVIEWS, UNAVAILABLE or DISABLED when Available is false.
    string? Reason,
    int ReviewCount,
    int RequiredCount,
    string? Summary,
    IReadOnlyList<string> Positives,
    IReadOnlyList<string> Negatives,
    DateTime? GeneratedAt,
    string Disclaimer);

#endregion

#region AIB-01 (ward)

public sealed record PermitPrecheckDto(
    long PermitId,
    string? PermitStatus,
    string? ContractStatus,
    string? SlotCode,
    bool ValidNow,
    bool SlotMatchesReport);

public sealed record VendorReportAssistDto(
    bool AiAvailable,
    string? SignageText,
    string? LocationText,
    string? PermitVisible,
    string? Description,
    IReadOnlyList<string> Flags,
    PermitPrecheckDto? PermitCheck,
    DateTime? GeneratedAt,
    long? AiLogId,
    string Disclaimer);

/// <param name="PriceEvidence">An overcharge's evidence: the posted price at purchase (server-computed),
/// the quantity, the amount charged and the difference.</param>
/// <param name="ReportsAgainstVendor90Days">Context only: never a score, never a reason to act alone.</param>
/// <param name="SuggestedViolationType">The type "Lập biên bản" pre-selects: rule-based, not AI.</param>
/// <param name="ViolationId">The biên bản raised from this report, if any.</param>
public sealed record WardVendorReportDto(
    long ReportId,
    string Reason,
    string? EvidenceUrl,
    string Status,
    DateTime CreatedAt,
    string? VendorName,
    string? SlotCode,
    string? AiExtractedLocation,
    VendorReportAssistDto Assist,
    string ReportType = "GENERAL",
    DateTime? DueAt = null,
    bool IsOverdue = false,
    long? VendorId = null,
    long? SlotId = null,
    long? ScannedPermitId = null,
    VendorReportPriceEvidenceDto? PriceEvidence = null,
    VendorReportOutcomeDto? Outcome = null,
    int ReportsAgainstVendor90Days = 0,
    string? SuggestedViolationType = null,
    long? ViolationId = null);

public sealed record VendorReportOutcomeDto(
    string Resolution,
    string? Note,
    decimal? RefundAmount,
    DateTime? ReviewedAt,
    string? ReviewedByName);

#endregion

/// <summary>The stored form of one AIB-01 run (what AIAssistanceLogs keeps and the ward screen reads back).</summary>
public sealed record StoredVendorReportAssist(
    string? SignageText,
    string? LocationText,
    string? PermitVisible,
    string? Description);

public sealed record StoredReviewSummary(string Summary, IReadOnlyList<string> Positives, IReadOnlyList<string> Negatives, int ReviewCount);

public sealed record StoredSearchIntent(
    string? Query,
    int? CategoryId,
    decimal? MinPrice,
    decimal? MaxPrice,
    bool? OpenNow,
    decimal? MinRating,
    bool NearMe,
    int? RadiusMeters,
    string? Sort,
    string Explanation);

public static class AiLogEntitiesMarketplace
{
    public const string Vendor = "Vendor";
    public const string Storefront = "Storefront";
    public const string VendorReport = "VendorReport";
    public const string Marketplace = "Marketplace";
}
