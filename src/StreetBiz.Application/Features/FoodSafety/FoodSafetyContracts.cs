namespace StreetBiz.Application.Features.FoodSafety;

/// <summary>
/// Food-safety (ATTP) certificate for some dishes of one storefront. The vendor submits,
/// the ward reviews and forwards the file to the department (Chi cục ATTP, outside the
/// system), then records the department's result. See db/StreetBiz_SQL_Server.sql.
/// </summary>
public static class FoodSafetyStatuses
{
    public const string Submitted = "SUBMITTED";
    public const string MoreInformationRequired = "MORE_INFORMATION_REQUIRED";
    public const string Forwarded = "FORWARDED";
    public const string Approved = "APPROVED";
    public const string Rejected = "REJECTED";
    public const string Withdrawn = "WITHDRAWN";

    /// <summary>Statuses in which an application still claims its dishes.</summary>
    public static readonly string[] Open = [Submitted, MoreInformationRequired, Forwarded];
}

public static class FoodSafetyDecisions
{
    public const string RequestInfo = "REQUEST_INFO";
    public const string Reject = "REJECT";
    public const string Forward = "FORWARD";
    public const string RecordApproved = "RECORD_APPROVED";
    public const string RecordRejected = "RECORD_REJECTED";
}

public static class FoodSafetyEvidenceTypes
{
    public static readonly string[] All = ["CERTIFICATE", "HEALTH_CHECK", "TRAINING", "PREMISES_PHOTO", "OTHER"];
}

/// <summary>How a dish stands with respect to ATTP, as shown on the vendor's menu.</summary>
public static class DishFoodSafetyStatuses
{
    public const string NotRequired = "NOT_REQUIRED";
    public const string Missing = "MISSING";
    public const string Pending = "PENDING";
    public const string Approved = "APPROVED";
}

public static class MenuRules
{
    /// <summary>
    /// A street stall sells a few signature dishes, which keeps food safety manageable.
    /// Counts every non-archived dish (admin-hidden ones too).
    /// </summary>
    public const int MaxActiveItemsPerStorefront = 5;
}

public sealed record FoodSafetyDishDto(long MenuItemId, string Name, string CategoryName, string? ImageUrl);

public sealed record FoodSafetyEvidenceDto(string EvidenceType, string FileUrl, DateTime UploadedAt);

public sealed record FoodSafetyApplicationDto(
    long ApplicationId,
    long StorefrontId,
    string StorefrontName,
    string VendorName,
    string Status,
    string? VendorNote,
    string? ReviewReason,
    DateTime? ReviewedAt,
    DateTime? ForwardedAt,
    string? DepartmentName,
    string? CertificateNumber,
    DateOnly? IssuedOn,
    DateOnly? ExpiresOn,
    bool IsExpired,
    string? ResultReason,
    DateTime? ResultRecordedAt,
    DateTime SubmittedAt,
    IReadOnlyList<FoodSafetyDishDto> Dishes,
    IReadOnlyList<FoodSafetyEvidenceDto> Evidence,
    IReadOnlyList<string> Actions);

public sealed record FoodSafetyEvidenceInput(string EvidenceType, string FileUrl);

public sealed record FoodSafetySubmitInput(
    long StorefrontId,
    long[] MenuItemIds,
    string? Note,
    FoodSafetyEvidenceInput[] Evidence);

public sealed record FoodSafetyDecisionInput(
    string Decision,
    string Reason,
    string ExpectedStatus,
    string? DepartmentName,
    string? CertificateNumber,
    DateOnly? IssuedOn,
    DateOnly? ExpiresOn);

public interface IFoodSafetyService
{
    Task<IReadOnlyList<FoodSafetyApplicationDto>> VendorList(CancellationToken ct);
    Task<FoodSafetyApplicationDto> VendorGet(long id, CancellationToken ct);
    Task<FoodSafetyApplicationDto> Submit(FoodSafetySubmitInput input, CancellationToken ct);
    Task<FoodSafetyApplicationDto> Resubmit(long id, FoodSafetySubmitInput input, CancellationToken ct);
    Task<FoodSafetyApplicationDto> Withdraw(long id, CancellationToken ct);

    Task<IReadOnlyList<FoodSafetyApplicationDto>> WardList(string? status, CancellationToken ct);
    Task<FoodSafetyApplicationDto> WardGet(long id, CancellationToken ct);
    Task<FoodSafetyApplicationDto> Decide(long id, FoodSafetyDecisionInput input, CancellationToken ct);

    /// <summary>True when the evidence file belongs to an ATTP file of a storefront in that ward.</summary>
    Task<bool> EvidenceBelongsToWardAsync(string fileUrl, int wardId, CancellationToken ct);
}
