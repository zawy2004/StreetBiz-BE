using StreetBiz.Application.Features.WardConfiguration;
using StreetBiz.Application.Features.WardSlots;

namespace StreetBiz.Application.Features.AiAssistance;

/// <summary>
/// AIAssistanceLogs.feature_code values this codebase writes. The column itself accepts any
/// "AI[A-Z]-NN" shape (db/StreetBiz_SQL_Server.sql), so a new AI feature needs no schema change --
/// just a new constant here.
/// </summary>
public static class AiFeatureCodes
{
    public const string DocumentCheck = "AIC-01";
    public const string InspectionPhoto = "AIC-02";
    public const string LegalDraft = "AIC-03";
    public const string ProposalSite = "AIC-04";
    public const string GeofenceDrift = "AIC-06";
    public const string ZonePrice = "AIC-07";
}

/// <summary>AIAssistanceLogs.entity_type values this codebase writes.</summary>
public static class AiLogEntities
{
    public const string Registration = "BusinessRegistration";
    public const string ScanLog = "PermitScanLog";
    public const string Slot = "SidewalkSlot";
    public const string Violation = "Violation";
    public const string Permit = "DigitalPermit";
    public const string Zone = "PricingZone";

    /// <summary>Used when a suggestion is about the ward as a whole rather than one record
    /// (e.g. an encroachment check with no slot given).</summary>
    public const string Ward = "WardUnit";
}

#region Logging core (BR-41)
/// <summary>A suggestion read back from the log, with staleness left to the caller to decide
/// (it compares its own freshly-computed input key against <paramref name="InputKey"/>).</summary>
public sealed record AiLogged<T>(long AiLogId, string? InputKey, T Output, DateTime CreatedAt, bool? Accepted);

public sealed record AiSuggestionFeedbackRequest(bool Accepted, string? Note);

public sealed record AiSuggestionFeedbackDto(long AiLogId, bool Accepted, DateTime ReviewedAt, string ReviewerName);

/// <summary>
/// Writes and reads AIAssistanceLogs (BR-41: every AI output is logged; the officer accepts or
/// rejects it, but never through the same action that approves/saves something else).
/// ai_output holds a {"inputKey","output"} envelope, so the same row is also the cache that
/// stops a page reload from re-billing the provider -- see AiAssistanceLogs.cs.
/// </summary>
public interface IAiAssistanceLogs
{
    /// <summary>Only ever called with a freshly-produced, AI-generated result -- never for a
    /// cache hit or a non-AI fallback (those are not "AI output" to log).</summary>
    Task<long> RecordAsync(
        string featureCode, string entityType, long entityId, string? inputKey,
        object output, decimal? confidence, CancellationToken ct);

    /// <summary>Null <paramref name="inputKey"/> returns the latest log for the entity
    /// regardless of key (used for "show the last assessment, even if it's stale now").
    /// A non-null key that does not match the stored one is also a miss.</summary>
    Task<AiLogged<T>?> FindLatestAsync<T>(
        string featureCode, string entityType, long entityId, string? inputKey, CancellationToken ct);

    /// <summary>Ward-scopes the log's underlying entity before accepting the review -- see each
    /// AiLogEntities case in AiAssistanceLogs.cs. Throws NotFoundException otherwise.</summary>
    Task<AiSuggestionFeedbackDto> ReviewAsync(
        WardActor actor, long aiLogId, AiSuggestionFeedbackRequest request, CancellationToken ct);
}
#endregion

#region AIC-04: proposed-slot feasibility
public sealed record AiProposalSiteInput(
    long SlotId,
    double Latitude,
    double Longitude,
    decimal? WidthMeters,
    decimal? LengthMeters,
    string ZoneName,
    TimeOnly? AvailableFrom,
    TimeOnly? AvailableTo,
    string? ProposalPhotoUrl,
    bool BoundaryVerified,
    IReadOnlyList<PlacementIssue> RuleChecks);

/// <summary>
/// AssessedAt/AiLogId are null until a value has actually been assigned by WardAiInsights (the
/// provider itself never sets them) -- see AiInsightRules.MergeProposal.
/// </summary>
public sealed record AiProposalAssessment(
    decimal? EstimatedSidewalkWidthMeters,
    decimal? RemainingPedestrianWidthMeters,
    string ObstructionLevel, // LOW | MEDIUM | HIGH | UNKNOWN
    string Recommendation, // LIKELY_FEASIBLE | NEEDS_SURVEY | LIKELY_INFEASIBLE
    int Confidence,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<PlacementIssue> RuleChecks,
    bool UsedSatelliteImage,
    bool UsedProposalPhoto,
    bool IsAiGenerated,
    DateTime? AssessedAt = null,
    long? AiLogId = null);

/// <summary>GET response: the latest logged assessment (if any), and whether the proposal's own
/// data has changed since -- the officer decides whether that is worth a fresh POST.</summary>
public sealed record AiProposalAssessmentView(AiProposalAssessment? Latest, bool IsStale);
#endregion

#region AIC-06: geofence drift
public sealed record GeofenceDriftScanDto(
    long ScanId, DateTime ScannedAt, string ScanContext, double DistanceMeters, double Latitude, double Longitude);

public sealed record GeofenceDriftItemDto(
    long PermitId,
    long ContractId,
    long SlotId,
    string SlotCode,
    string? ZoneName,
    string VendorName,
    int ScanCount,
    int OffSiteCount,
    double MaxDistanceMeters,
    double? MeanOffsetMeters,
    double? MeanOffsetBearingDegrees,
    string Level, // WATCH | DRIFT
    string Pattern, // CONSISTENT_DIRECTION | SCATTERED
    DateTime LastOffSiteAt,
    IReadOnlyList<GeofenceDriftScanDto> Scans,
    string Explanation,
    bool IsAiGenerated,
    long? AiLogId);

public sealed record GeofenceDriftReportDto(int WindowDays, double ToleranceMeters, IReadOnlyList<GeofenceDriftItemDto> Items);

/// <summary>What the Groq narrative is given for one DRIFT permit -- deliberately only the
/// numbers WardAiInsights already computed, not raw scan rows, so the model cannot invent a
/// location the system did not measure.</summary>
public sealed record GeofenceDriftFacts(
    long PermitId, string SlotCode, string? ZoneName, int ScanCount, int OffSiteCount,
    double MaxDistanceMeters, double? MeanOffsetMeters, double? MeanOffsetBearingDegrees, string Pattern);
#endregion

#region AIC-07: zone price suggestion
public sealed record ZonePriceSuggestionDto(
    int ZoneId,
    string ZoneName,
    decimal CurrentPricePerDay,
    int WindowDays,
    int SlotCount,
    int OccupiedSlotDays,
    int AvailableSlotDays,
    double OccupancyPercent,
    int ApplicationsInWindow,
    int RejectedApplications,
    int PendingApplications,
    int ActiveHolds,
    string Direction, // RAISE | LOWER | KEEP | INSUFFICIENT_DATA
    decimal BaselinePricePerDay,
    decimal SuggestedPricePerDay,
    decimal MinAllowedPricePerDay,
    decimal MaxAllowedPricePerDay,
    string Explanation,
    bool IsAiGenerated,
    long? AiLogId);

/// <summary>What the Groq narrative is given -- the baseline and clamp are already decided;
/// the model may only propose a number inside [MinAllowedPricePerDay, MaxAllowedPricePerDay].</summary>
public sealed record ZonePriceFacts(
    int ZoneId, string ZoneName, decimal CurrentPricePerDay, int WindowDays, int SlotCount,
    int OccupiedSlotDays, int AvailableSlotDays, double OccupancyPercent, int ApplicationsInWindow,
    int RejectedApplications, int PendingApplications, int ActiveHolds, string Direction,
    decimal BaselinePricePerDay, decimal MinAllowedPricePerDay, decimal MaxAllowedPricePerDay,
    TimeOnly? AvailableFrom, TimeOnly? AvailableTo);

public sealed record AiPriceAdvice(decimal? ProposedPricePerDay, string Explanation);
#endregion

#region Service interface
/// <summary>Orchestrates AIC-04/06/07: loads ward-scoped data, calls the deterministic rules in
/// AiInsightRules, calls IAiComplianceService for the parts that need a model, and logs through
/// IAiAssistanceLogs. One concrete service for three concrete features (not folded into
/// IWardComplianceService/IWardConfigurationService) so their constructors and test fakes do not
/// all need an AI dependency.</summary>
public interface IWardAiInsights
{
    /// <summary>Never calls the AI provider -- just the latest logged result (if any) plus a
    /// staleness flag. Throws NotFoundException if the proposal is not in the officer's ward.</summary>
    Task<AiProposalAssessmentView> GetProposalAssessmentAsync(WardActor actor, long slotId, CancellationToken ct);

    /// <summary>The officer explicitly asked for a fresh (or reused, if nothing changed)
    /// assessment. Throws WardException(409) if the proposal is no longer PENDING.</summary>
    Task<AiProposalAssessment> RunProposalAssessmentAsync(WardActor actor, long slotId, CancellationToken ct);

    Task<GeofenceDriftReportDto> GetGeofenceDriftAsync(WardActor actor, CancellationToken ct);

    Task<ZonePriceSuggestionDto> GetZonePriceSuggestionAsync(WardActor actor, int zoneId, CancellationToken ct);

    /// <summary>Ward-scoped wrapper around IAiComplianceService.AnalyzeInspectionPhotoAsync for
    /// POST ai/encroachment-check: with a SlotId it loads the slot's own dimensions from the
    /// ward's own data (ignoring any client-supplied width/length) and logs against the slot;
    /// without one it logs against the ward itself. Throws NotFoundException if SlotId is given
    /// but is not in the officer's ward.</summary>
    Task<StreetBiz.Application.Features.WardCompliance.AiEncroachmentResult> CheckEncroachmentAsync(
        WardActor actor, string photoUrl, double? slotWidth, double? slotLength, long? slotId, CancellationToken ct);
}
#endregion
