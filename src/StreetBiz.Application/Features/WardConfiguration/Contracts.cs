using System.Globalization;
using System.Text.RegularExpressions;
using StreetBiz.Application.Features.WardSlots;

namespace StreetBiz.Application.Features.WardConfiguration;

#region WARD-03 Penalty schedule
public sealed record PenaltyRateDto(
    int ScheduleId,
    decimal Amount,
    long? BracketMin,
    long? BracketMax,
    string? LegalBasis,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    DateTime CreatedAt,
    bool IsInUse,
    /// <summary>The officer who set this rate (PenaltyFeeSchedules.created_by), for BR-46 traceability.</summary>
    string ActorName);

public sealed record WardPenaltyTypeDto(
    string ViolationType,
    string Description,
    bool IsActive,
    bool HasLegalBasis,
    PenaltyRateDto? Current,
    PenaltyRateDto? Scheduled);

public sealed record SetPenaltyRateRequest(
    string ViolationType,
    string DocumentRef,
    string Article,
    string Clause,
    string? Point,
    string Behavior,
    long BracketMin,
    long BracketMax,
    DateOnly EffectiveFrom,
    int? ExpectedCurrentScheduleId);
#endregion

#region WARD-02 Pricing zones
public sealed record ZoneFeeComponentInput(string ComponentName, string CalcBasis, long UnitAmount);

public sealed record ZoneFeeComponentView(int ComponentId, string ComponentName, string CalcBasis, decimal UnitAmount, int SortOrder);

public sealed record WardZoneDto(
    int ZoneId,
    string ZoneName,
    string? ZoneCode,
    decimal PricePerDay,
    TimeOnly? AvailableFrom,
    TimeOnly? AvailableTo,
    bool IsOvernight,
    string? RegulationRef,
    string? SegmentFrom,
    string? SegmentTo,
    DateOnly? ApplicationDeadline,
    int SlotCount,
    int ActiveSlotCount,
    int FeatureCount,
    IReadOnlyList<ZoneFeeComponentView> FeeComponents,
    string VersionToken,
    string PriceDisplayUnit,
    decimal? PricePerMonth,
    string RentalMode,
    DateOnly? EventStartDate,
    DateOnly? EventEndDate);

/// <summary>
/// Regulation parts are composed server-side into regulation_ref. On update, leaving all three
/// null keeps the stored reference unchanged.
/// </summary>
public sealed record UpsertZoneRequest(
    string ZoneName,
    string ZoneCode,
    long PricePerDay,
    TimeOnly? AvailableFrom,
    TimeOnly? AvailableTo,
    string? RegulationNumber,
    DateOnly? RegulationIssuedOn,
    string? RegulationIssuer,
    string? SegmentFrom,
    string? SegmentTo,
    DateOnly? ApplicationDeadline,
    IReadOnlyList<ZoneFeeComponentInput> FeeComponents,
    string? ChangeReason,
    string? VersionToken,
    /// <summary>"DAY" (default) or "MONTH". When MONTH, PricePerMonth is required and
    /// PricePerDay is derived from it (ROUND(PricePerMonth / 30, 0)) rather than taken as-is --
    /// price_per_day stays the sole input FeeQuoteCalculator/FeeInstalmentPlanner read.</summary>
    string PriceDisplayUnit = "DAY",
    long? PricePerMonth = null,
    /// <summary>"STANDARD" (default, long-term) or "EVENT" (short-term/pop-up, keeps the
    /// existing day-by-day rental flow). EVENT requires EventStartDate/EventEndDate and may
    /// not use PriceDisplayUnit MONTH.</summary>
    string RentalMode = "STANDARD",
    DateOnly? EventStartDate = null,
    DateOnly? EventEndDate = null);

public sealed record ZoneImpactPreviewRequest(
    long PricePerDay,
    TimeOnly? AvailableFrom,
    TimeOnly? AvailableTo,
    IReadOnlyList<ZoneFeeComponentInput> FeeComponents);

public sealed record ZoneImpactItem(string Kind, long Id, string SlotCode, string VendorName, int TermDays, decimal CurrentTotal, decimal NewTotal);

public sealed record ZoneImpactPreviewDto(
    /// <summary>True when the price-per-day OR the fee components changed -- either one changes
    /// the total a pending application or renewal will be charged once approved.</summary>
    bool AmountChanged,
    bool HoursChanged,
    IReadOnlyList<ZoneImpactItem> PendingApplications,
    IReadOnlyList<ZoneImpactItem> OpenRenewals,
    int ActiveContractsAffectedByHours,
    decimal TotalDelta,
    int VendorsToNotify);

public sealed record ConfigHistoryEntryDto(long AuditId, string Action, string ActorName, DateTime CreatedAt, string? Details);
#endregion

#region WARD-01 Slot grid & street features
public sealed record PlacementIssue(
    string Severity,
    string Code,
    string Message,
    int? FeatureId,
    long? SlotId,
    double? DistanceMeters);

public static class PlacementSeverities
{
    public const string Block = "BLOCK";
    public const string Warn = "WARN";
}

public sealed record WardSlotDto(
    long SlotId,
    string SlotCode,
    int ZoneId,
    string ZoneName,
    decimal Latitude,
    decimal Longitude,
    decimal? WidthMeters,
    decimal? LengthMeters,
    string Status,
    string Source,
    bool HasPower,
    bool HasWater,
    bool HasTrashBin,
    string? BusinessCategory,
    bool CanHardDelete,
    bool CanEditGeometry,
    string VersionToken);

public sealed record WardStreetFeatureDto(
    int FeatureId,
    int ZoneId,
    string FeatureType,
    string Label,
    decimal Latitude,
    decimal Longitude,
    bool BlocksBusiness,
    string? Note,
    double? ClearanceMeters,
    string VersionToken);

public sealed record WardSlotGridDto(
    IReadOnlyList<WardSlotDto> Slots,
    IReadOnlyList<WardStreetFeatureDto> Features,
    bool BoundaryConfigured,
    bool ClearanceCheckEnabled);

public sealed record SlotPlacementInput(
    int ZoneId,
    decimal Latitude,
    decimal Longitude,
    decimal WidthMeters,
    decimal LengthMeters);

public sealed record PlacementCheckDto(bool BoundaryVerified, IReadOnlyList<PlacementIssue> Issues);

public sealed record CreateSlotRequest(
    int ZoneId,
    string? SlotCode,
    decimal Latitude,
    decimal Longitude,
    decimal WidthMeters,
    decimal LengthMeters,
    bool HasPower,
    bool HasWater,
    bool HasTrashBin,
    string? BusinessCategory,
    bool AcknowledgeWarnings,
    string? WarningReason);

public sealed record UpdateSlotRequest(
    int ZoneId,
    string SlotCode,
    decimal Latitude,
    decimal Longitude,
    decimal WidthMeters,
    decimal LengthMeters,
    bool HasPower,
    bool HasWater,
    bool HasTrashBin,
    string? BusinessCategory,
    string VersionToken,
    bool AcknowledgeWarnings,
    string? WarningReason);

public sealed record SetSlotStatusRequest(string Status, string Reason, string VersionToken);

public sealed record SlotMutationResultDto(WardSlotDto Slot, PlacementCheckDto Check);

public sealed record BatchPreviewRequest(
    int ZoneId,
    decimal StartLatitude,
    decimal StartLongitude,
    decimal EndLatitude,
    decimal EndLongitude,
    decimal WidthMeters,
    decimal LengthMeters,
    decimal GapMeters);

public sealed record BatchCandidateDto(int Index, string ProposedCode, decimal Latitude, decimal Longitude, IReadOnlyList<PlacementIssue> Issues);

public sealed record BatchPreviewDto(bool BoundaryVerified, IReadOnlyList<BatchCandidateDto> Candidates);

public sealed record BatchSlotPosition(decimal Latitude, decimal Longitude);

public sealed record BatchCreateRequest(
    int ZoneId,
    IReadOnlyList<BatchSlotPosition> Positions,
    decimal WidthMeters,
    decimal LengthMeters,
    bool HasPower,
    bool HasWater,
    bool HasTrashBin,
    string? BusinessCategory,
    bool AcknowledgeWarnings,
    string? WarningReason);

public sealed record UpsertStreetFeatureRequest(
    int ZoneId,
    string FeatureType,
    string Label,
    decimal Latitude,
    decimal Longitude,
    bool BlocksBusiness,
    string? Note,
    string? VersionToken);

public sealed record StreetFeatureMutationResultDto(WardStreetFeatureDto Feature, IReadOnlyList<PlacementIssue> AffectedSlots);
#endregion

#region WardCompliancePolicy (Phase A)
/// <summary>Null fields mean the feature is off for this ward -- no "consider revoking" banner,
/// no overdue-penalty reminder sweep. A ward opts in explicitly.</summary>
public sealed record WardCompliancePolicyDto(
    int? ViolationThresholdCount,
    int? ViolationWindowDays,
    int? UnpaidPenaltyGraceDays,
    DateTime? UpdatedAt,
    string? UpdatedByName);

public sealed record UpsertWardCompliancePolicyRequest(
    int? ViolationThresholdCount,
    int? ViolationWindowDays,
    int? UnpaidPenaltyGraceDays);
#endregion

public static class StreetFeatureTypes
{
    public static readonly string[] All = ["TRANSFORMER", "HYDRANT", "TREE", "LIGHT_POLE", "BUS_STOP", "PARKING"];
}

public static class ZoneRegulationText
{
    /// <summary>PricingZones.regulation_ref is NVARCHAR(120).</summary>
    public const int MaxLength = 120;

    public static string Compose(string number, DateOnly issuedOn, string issuer) =>
        $"{number.Trim()} ngày {issuedOn.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)} của {issuer.Trim()}";
}

public static class ZoneHours
{
    public static bool IsOvernight(TimeOnly? from, TimeOnly? to) => from is not null && to is not null && from > to;
}

/// <summary>
/// Formats and reads the legal_basis text. The bracket is embedded as "(khung A - Bđ)" -- the
/// same shape the demo seed already uses -- so rows written before WARD-03 existed still parse.
/// </summary>
public static partial class LegalBasisText
{
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");

    public const int MaxLength = 500;

    public static string Format(string documentRef, string article, string clause, string? point, string behavior, long min, long max)
    {
        var location = $"Điều {article.Trim()}, khoản {clause.Trim()}";
        if (!string.IsNullOrWhiteSpace(point))
            location += $", điểm {point.Trim()}";
        return $"{documentRef.Trim()}, {location}: {behavior.Trim()} (khung {min.ToString("N0", Vi)} - {max.ToString("N0", Vi)}đ)";
    }

    public static (long Min, long Max)? TryParseBracket(string? legalBasis)
    {
        if (string.IsNullOrWhiteSpace(legalBasis)) return null;
        var match = BracketPattern().Match(legalBasis);
        if (!match.Success) return null;
        var min = long.Parse(match.Groups[1].Value.Replace(".", "", StringComparison.Ordinal), CultureInfo.InvariantCulture);
        var max = long.Parse(match.Groups[2].Value.Replace(".", "", StringComparison.Ordinal), CultureInfo.InvariantCulture);
        return (min, max);
    }

    /// <summary>
    /// Luật XLVPHC Điều 23 khoản 4: without mitigating/aggravating circumstances the specific
    /// fine is the average of the bracket. Rounded down to whole VND, in the violator's favour.
    /// </summary>
    public static long Midpoint(long min, long max) => (min + max) / 2;

    [GeneratedRegex(@"\(khung\s+([\d.]+)\s*-\s*([\d.]+)\s*đ\)")]
    private static partial Regex BracketPattern();
}

public interface IWardConfigurationService
{
    // WARD-03
    Task<IReadOnlyList<WardPenaltyTypeDto>> ListPenaltyOverviewAsync(WardActor actor, CancellationToken ct);
    Task<IReadOnlyList<PenaltyRateDto>> ListPenaltyHistoryAsync(WardActor actor, string violationType, CancellationToken ct);
    Task<WardPenaltyTypeDto> SetPenaltyRateAsync(WardActor actor, SetPenaltyRateRequest request, CancellationToken ct);
    Task<WardPenaltyTypeDto> CancelScheduledPenaltyRateAsync(WardActor actor, int scheduleId, CancellationToken ct);

    // WARD-02
    Task<IReadOnlyList<WardZoneDto>> ListZonesAsync(WardActor actor, CancellationToken ct);
    Task<WardZoneDto> GetZoneAsync(WardActor actor, int zoneId, CancellationToken ct);
    Task<WardZoneDto> CreateZoneAsync(WardActor actor, UpsertZoneRequest request, CancellationToken ct);
    Task<WardZoneDto> UpdateZoneAsync(WardActor actor, int zoneId, UpsertZoneRequest request, CancellationToken ct);
    Task DeleteZoneAsync(WardActor actor, int zoneId, string versionToken, CancellationToken ct);
    Task<ZoneImpactPreviewDto> PreviewZoneImpactAsync(WardActor actor, int zoneId, ZoneImpactPreviewRequest request, CancellationToken ct);
    Task<IReadOnlyList<ConfigHistoryEntryDto>> ListZoneHistoryAsync(WardActor actor, int zoneId, CancellationToken ct);

    // WARD-01
    Task<WardSlotGridDto> GetSlotGridAsync(WardActor actor, int? zoneId, CancellationToken ct);
    Task<PlacementCheckDto> CheckPlacementAsync(WardActor actor, SlotPlacementInput input, long? ignoreSlotId, CancellationToken ct);
    Task<SlotMutationResultDto> CreateSlotAsync(WardActor actor, CreateSlotRequest request, CancellationToken ct);
    Task<SlotMutationResultDto> UpdateSlotAsync(WardActor actor, long slotId, UpdateSlotRequest request, CancellationToken ct);
    Task<WardSlotDto> SetSlotStatusAsync(WardActor actor, long slotId, SetSlotStatusRequest request, CancellationToken ct);
    Task DeleteSlotAsync(WardActor actor, long slotId, string versionToken, CancellationToken ct);
    Task<BatchPreviewDto> PreviewBatchAsync(WardActor actor, BatchPreviewRequest request, CancellationToken ct);
    Task<IReadOnlyList<WardSlotDto>> CreateBatchAsync(WardActor actor, BatchCreateRequest request, CancellationToken ct);
    Task<IReadOnlyList<ConfigHistoryEntryDto>> ListSlotHistoryAsync(WardActor actor, long slotId, CancellationToken ct);
    Task<StreetFeatureMutationResultDto> CreateFeatureAsync(WardActor actor, UpsertStreetFeatureRequest request, CancellationToken ct);
    Task<StreetFeatureMutationResultDto> UpdateFeatureAsync(WardActor actor, int featureId, UpsertStreetFeatureRequest request, CancellationToken ct);
    Task DeleteFeatureAsync(WardActor actor, int featureId, string versionToken, CancellationToken ct);

    // Phase A
    Task<WardCompliancePolicyDto> GetCompliancePolicyAsync(WardActor actor, CancellationToken ct);
    Task<WardCompliancePolicyDto> UpsertCompliancePolicyAsync(WardActor actor, UpsertWardCompliancePolicyRequest request, CancellationToken ct);
}
