using StreetBiz.Application.Features.VendorKyc;
using StreetBiz.Application.Features.WardSlots;

namespace StreetBiz.Application.Features.WardCompliance;

#region DTOs - Enrollment / Registration
public sealed record WardEnrollmentListItemDto(
    string Id,
    string DisplayName,
    string OwnerName,
    string? IdNumber,
    string VendorType,
    string Status,
    string Address,
    DateTime CreatedAt,
    bool FastTrack);

public sealed record WardEvidenceDto(
    long EvidenceId,
    string Type,
    string Label,
    string FileUrl);

public sealed record WardEnrollmentDetailDto(
    string Id,
    string DisplayName,
    string OwnerName,
    string? IdNumber,
    string VendorType,
    string Status,
    string Address,
    double? Latitude,
    double? Longitude,
    DateTime CreatedAt,
    string? ReviewReason,
    string? ReviewedBy,
    DateTime? ReviewedAt,
    IReadOnlyList<WardEvidenceDto> Evidence,
    AiDocumentCheckResult? AiCheck,
    // ---- Mẫu số 01 Phụ lục II, Thông tư 68/2025/TT-BTC: hồ sơ đầy đủ để cán bộ thẩm định ----
    WardOwnerProfileDto OwnerProfile,
    WardBusinessProfileDto BusinessProfile,
    DateTime? FoodSafetyCommitmentAt,
    IReadOnlyList<WardHouseholdMemberDto> HouseholdMembers,
    // ---- KYC gate: cán bộ phải tự xác nhận trước khi được Duyệt (BR-41) ----
    bool IdentityVerified,
    DateTime? IdentityVerifiedAt,
    string? IdentityVerifiedByName,
    string? IdentityVerificationNote,
    /// <summary>Server-recorded eKYC scores (CCCD OCR, face match) for this registration.
    /// Evidence for the officer's own decision -- never a substitute for it.</summary>
    IReadOnlyList<KycCheckRecord> KycChecks);

public sealed record WardOwnerProfileDto(
    DateOnly? DateOfBirth,
    string? Gender,
    string? Ethnicity,
    string? Nationality,
    string? IdType,
    DateOnly? IdIssuedDate,
    string? IdIssuedPlace,
    string? PermanentAddress,
    string? ContactAddress);

public sealed record WardBusinessProfileDto(
    string? BusinessLine,
    string? BusinessLineCode,
    decimal? CapitalAmount,
    int? LaborCount,
    DateOnly? PlannedStartDate);

public sealed record WardHouseholdMemberDto(
    string FullName,
    DateOnly? DateOfBirth,
    string? IdNumber,
    string? RelationshipToOwner,
    decimal? CapitalContribution);

public sealed record WardEnrollmentDecision(
    string Decision,
    string Reason,
    string ExpectedStatus);

/// <summary>
/// A Ward Authority officer's manual confirmation that they compared the vendor in person (or
/// the uploaded photo) against the physical/chip CCCD. Required before DecideEnrollmentAsync
/// will accept an APPROVE decision -- AI-OCR alone never satisfies this (BR-41).
/// </summary>
public sealed record ConfirmEnrollmentIdentity(string Note);
#endregion

#region DTOs - Temporary Sidewalk Usage Permit / Application
public sealed record WardRentalApplicationListItemDto(
    string Id,
    string ApplicationMethod,
    int RequestedTermDays,
    string Status,
    string VendorName,
    string SlotCode,
    string SlotStreet,
    decimal PricePerDay,
    DateTime CreatedAt);

public sealed record WardRentalApplicationDetailDto(
    string Id,
    string ApplicationMethod,
    int RequestedTermDays,
    string Status,
    long RegistrationId,
    string RegistrationStatus,
    string VendorName,
    string VendorPhone,
    long SlotId,
    string SlotCode,
    string SlotStreet,
    decimal SlotWidth,
    decimal SlotLength,
    decimal PricePerDay,
    DateTime CreatedAt,
    string? ReviewReason,
    string? ReviewedBy,
    DateTime? ReviewedAt,
    bool CanApprove,
    IReadOnlyList<string> Blockers);

public sealed record WardRentalApplicationDecision(
    string Decision,
    string Reason,
    string ExpectedStatus);
#endregion

#region DTOs - On-site Inspection & Permit Verification
public sealed record InspectWardPermitRequest(
    string PermitCodeOrPayload,
    double? Latitude,
    double? Longitude,
    string? InspectionPhotoUrl);

public sealed record InspectWardPermitResult(
    bool Found,
    bool IsValid,
    string EffectiveStatus,
    long? PermitId,
    long? ContractId,
    long? VendorId,
    string? VendorName,
    long? SlotId,
    string? SlotCode,
    string? SlotStreet,
    decimal? Width,
    decimal? Length,
    DateOnly? StartDate,
    DateOnly? EndDate,
    double? SlotLatitude,
    double? SlotLongitude,
    double? DistanceMeters,
    bool IsLocationMatched,
    string? LocationWarning,
    AiEncroachmentResult? AiVisionResult);

public sealed record WardPermitActionRequest(
    string Action,
    string Reason);
#endregion

#region DTOs - Violations & Sanctions
public sealed record PenaltyScheduleItemDto(
    int ScheduleId,
    string ViolationType,
    string ViolationTypeName,
    decimal PenaltyAmount,
    string? LegalBasis);

public sealed record RecordWardViolationRequest(
    long? ContractId,
    long? SlotId,
    long? VendorId,
    string ViolationType,
    string Description,
    string? EvidenceUrl);

public sealed record SanctionWardViolationRequest(
    int PenaltyScheduleId,
    string DecisionNumber,
    /// <summary>
    /// Self-declared name/title of the person exercising sanction authority (Chairman/
    /// Vice-Chairman of the Ward People's Committee, or a written delegate -- the patrolling
    /// officer who filed the violation record does not have this authority under the Law on
    /// Handling of Administrative Violations). Not enforced via RBAC in this iteration; see
    /// docs_system/features/ward-review-permit-compliance.md section 6.3.
    /// </summary>
    string SignerName,
    string SignerTitle,
    string? Notes);

public sealed record WardViolationListItemDto(
    long ViolationId,
    long? ContractId,
    string? SlotCode,
    string? VendorName,
    string ViolationType,
    string ViolationTypeName,
    string Status,
    decimal? PenaltyAmount,
    DateTime RecordedAt,
    string RecordedByName);

public sealed record WardViolationDetailDto(
    long ViolationId,
    long? ContractId,
    long? SlotId,
    string? SlotCode,
    long? VendorId,
    string? VendorName,
    string ViolationType,
    string ViolationTypeName,
    string? Description,
    string? EvidenceUrl,
    DateTime RecordedAt,
    string RecordedByName,
    string Status,
    decimal? PenaltyAmount,
    string? SanctionDecisionNumber,
    string? SignerName,
    string? SignerTitle,
    DateTime? SanctionedAt,
    int RecentViolationCount90Days,
    AiLegalSuggestion? AiSuggestion);
#endregion

#region DTOs - Insights (rule-based, no LLM)
public sealed record RiskScoreBreakdownItem(string Reason, int Points);

public sealed record WardRiskQueueItemDto(
    string RegistrationId,
    string DisplayName,
    int Score,
    IReadOnlyList<RiskScoreBreakdownItem> Breakdown);

public sealed record WardPatrolHeatmapPointDto(
    int? ZoneId,
    string? ZoneName,
    int DayOfWeek,
    int HourOfDay,
    int ViolationCount);
#endregion

#region AI Assistant Models
public sealed record AiDocumentCheckResult(
    int MatchPercentage,
    bool IsMatch,
    bool NeedsManualVerification,
    string Summary,
    IReadOnlyList<string> Discrepancies,
    bool IsAiGenerated);

public sealed record AiEncroachmentResult(
    bool DetectedEncroachment,
    double EncroachmentDistanceCm,
    string Analysis,
    IReadOnlyList<string> VisualCues,
    bool IsAiGenerated);

/// <summary>
/// Fields mirror Mau MBB01 (Nghi dinh 118/2021/ND-CP) instead of one free-form paragraph, so
/// the officer's biên bản is ready to transcribe onto the standard form. LegalBasis is always
/// copied verbatim from the matched PenaltyFeeSchedules row -- the AI drafts HanhViViPham and
/// BienPhapKhacPhuc only, never the legal citation itself.
/// </summary>
public sealed record AiLegalSuggestion(
    string ViolationType,
    int? PenaltyScheduleId,
    string? LegalBasis,
    decimal? SuggestedPenaltyAmount,
    string HanhViViPham,
    string BienPhapKhacPhuc,
    bool IsAiGenerated);

public sealed record AiIdExtractionResult(
    string? IdNumber,
    string? FullName,
    string? Address,
    int ConfidencePercent,
    bool IsAiGenerated,
    string? Source = null,
    string? TamperingAlert = null);
#endregion

#region Service Interfaces
public interface IWardComplianceService
{
    // Enrollment / Registration
    Task<IReadOnlyList<WardEnrollmentListItemDto>> ListEnrollmentsAsync(WardActor actor, string? status, int page, CancellationToken ct);
    Task<WardEnrollmentDetailDto> GetEnrollmentDetailAsync(WardActor actor, long registrationId, CancellationToken ct);
    Task<WardEnrollmentDetailDto> DecideEnrollmentAsync(WardActor actor, long registrationId, WardEnrollmentDecision decision, CancellationToken ct);

    /// <summary>
    /// Records the officer's manual identity-verification confirmation (BR-41 KYC gate).
    /// DecideEnrollmentAsync refuses an APPROVE decision until this has been called.
    /// </summary>
    Task<WardEnrollmentDetailDto> ConfirmIdentityAsync(WardActor actor, long registrationId, ConfirmEnrollmentIdentity request, CancellationToken ct);

    /// <summary>
    /// Re-runs AI-OCR against the registration's own stored evidence (server-authoritative --
    /// never trusts a client-supplied evidence list or declared name/address). Refuses with an
    /// honest, non-AI result when biometric_consent_at is not set.
    /// </summary>
    Task<AiDocumentCheckResult> ReRunDocumentCheckAsync(WardActor actor, long registrationId, CancellationToken ct);

    // Rental Applications
    Task<IReadOnlyList<WardRentalApplicationListItemDto>> ListRentalApplicationsAsync(WardActor actor, string? status, int page, CancellationToken ct);
    Task<WardRentalApplicationDetailDto> GetRentalApplicationDetailAsync(WardActor actor, long applicationId, CancellationToken ct);
    Task<WardRentalApplicationDetailDto> DecideRentalApplicationAsync(WardActor actor, long applicationId, WardRentalApplicationDecision decision, CancellationToken ct);

    // On-site Inspection & Permit
    Task<InspectWardPermitResult> InspectPermitAsync(WardActor actor, InspectWardPermitRequest request, CancellationToken ct);
    Task<bool> ExecutePermitActionAsync(WardActor actor, long permitId, WardPermitActionRequest request, CancellationToken ct);

    // Violations & Sanctions
    Task<IReadOnlyList<PenaltyScheduleItemDto>> ListPenaltySchedulesAsync(WardActor actor, CancellationToken ct);
    Task<IReadOnlyList<WardViolationListItemDto>> ListViolationsAsync(WardActor actor, string? status, int page, CancellationToken ct);
    Task<WardViolationDetailDto> GetViolationDetailAsync(WardActor actor, long violationId, CancellationToken ct);
    Task<WardViolationDetailDto> RecordViolationAsync(WardActor actor, RecordWardViolationRequest request, CancellationToken ct);
    Task<WardViolationDetailDto> SanctionViolationAsync(WardActor actor, long violationId, SanctionWardViolationRequest request, CancellationToken ct);

    // Insights (rule-based)
    Task<IReadOnlyList<WardRiskQueueItemDto>> GetRiskQueueAsync(WardActor actor, CancellationToken ct);
    Task<IReadOnlyList<WardPatrolHeatmapPointDto>> GetPatrolHeatmapAsync(WardActor actor, CancellationToken ct);
}

public interface IAiComplianceService
{
    /// Extracts id_number/full_name/address from a citizen ID photo (multimodal). Never compares
    /// against phone number -- the caller passes only the evidence file URLs to analyze.
    Task<AiIdExtractionResult> ExtractIdDocumentAsync(IReadOnlyList<WardEvidenceDto> evidence, CancellationToken ct);

    /// Compares AI-extracted identity data against the vendor's self-declared profile.
    Task<AiDocumentCheckResult> CompareDeclaredProfileAsync(
        string declaredName, string? extractedIdNumber, string declaredAddress,
        AiIdExtractionResult extraction, CancellationToken ct);

    /// Multimodal analysis of an on-site inspection photo against the permitted slot dimensions.
    /// photoUrl must resolve to real image bytes fetchable by this service -- never passed as a
    /// bare string inside a text-only prompt.
    Task<AiEncroachmentResult> AnalyzeInspectionPhotoAsync(string photoUrl, double? slotWidth, double? slotLength, CancellationToken ct);

    /// Classifies a violation description into a violation_type and drafts record wording FROM a
    /// pre-existing PenaltyFeeSchedules row -- never invents a legal citation or amount itself.
    Task<AiLegalSuggestion> ClassifyAndDraftAsync(
        string? description, IReadOnlyList<PenaltyScheduleItemDto> availableSchedules, CancellationToken ct);

    /// Answers vendor or officer regulatory inquiries about Road Law 2024, Decree 165/2024/ND-CP,
    /// sidewalk usage permits, and compliance guidelines using fast Groq LLM reasoning.
    Task<string> AnswerVendorAssistantAsync(string question, string? context, CancellationToken ct);
}
#endregion
