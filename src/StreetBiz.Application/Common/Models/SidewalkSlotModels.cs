namespace StreetBiz.Application.Common.Models;

/// <summary>Application-facing projection of a sidewalk slot, joined with its pricing zone.</summary>
public sealed record SlotRow(
    long SlotId,
    string SlotCode,
    int ZoneId,
    string ZoneName,
    int WardUnitId,
    decimal Latitude,
    decimal Longitude,
    decimal? WidthMeters,
    decimal? LengthMeters,
    string SlotStatus,
    string Source,
    decimal PricePerDay,
    TimeOnly? AvailableFrom,
    TimeOnly? AvailableTo,
    /// <summary>WARD-02's "hạn nhận đơn": null means no deadline; a past date means the zone is
    /// closed to new applications (existing applications, renewals and contracts are unaffected).</summary>
    DateOnly? ApplicationDeadline,
    string? ImageUrl,
    bool HasPower,
    bool HasWater,
    bool HasTrashBin,
    string? BusinessCategory,
    string? TenantName,
    DateTime? HoldExpiresAt,
    // ---- Phase B/C: the zone's rental-term model, so the vendor's apply form can offer the
    // right input (month quick-select vs free-text days) and the right pricing caption.
    // Defaulted so existing call sites (tests built before this feature) keep compiling. ----
    string PriceDisplayUnit = "DAY",
    decimal? PricePerMonth = null,
    string RentalMode = "STANDARD",
    DateOnly? EventEndDate = null);

/// <summary>
/// The slot search filter. The bounding box is optional in decimal degrees -- a zoneId lookup
/// needs no area at all -- and callers refine any SQL-side bbox result with
/// GeoMath.DistanceMeters for an exact-radius answer.
/// </summary>
public sealed record SlotSearchArea(
    decimal? MinLatitude,
    decimal? MaxLatitude,
    decimal? MinLongitude,
    decimal? MaxLongitude,
    int? WardUnitId,
    int? ZoneId,
    bool IncludeUnavailable);

/// <summary>Fields supplied by a vendor proposing a new slot at an unlisted location (SIDE-11).</summary>
public sealed record NewSlotProposal(
    int ZoneId,
    decimal Latitude,
    decimal Longitude,
    decimal? WidthMeters,
    decimal? LengthMeters,
    string ProposalPhotoUrl);

/// <summary>Application-facing projection of a vendor-proposed slot and its review state.</summary>
public sealed record SlotProposalRow(
    long SlotId,
    string SlotCode,
    decimal Latitude,
    decimal Longitude,
    string ProposalReviewStatus,
    string ProposalPhotoUrl,
    string? ProposalReviewReason,
    DateTime CreatedAt);

/// <summary>A pricing zone's descriptive data, its ward's contact, fee table and street features.</summary>
public sealed record ZoneDetailRow(
    int ZoneId,
    string ZoneName,
    string? ZoneCode,
    string? RegulationRef,
    string? SegmentFrom,
    string? SegmentTo,
    DateOnly? ApplicationDeadline,
    int WardUnitId,
    string WardName,
    string? ContactName,
    string? ContactPhone,
    decimal PricePerDay,
    TimeOnly? AvailableFrom,
    TimeOnly? AvailableTo,
    IReadOnlyList<FeeComponentRow> FeeComponents,
    IReadOnlyList<StreetFeatureRow> Features);

/// <summary>One line of a zone's fee table. <c>CalcBasis</c> is PER_DAY or PER_TERM.</summary>
public sealed record FeeComponentRow(
    int ComponentId,
    string ComponentName,
    string CalcBasis,
    decimal UnitAmount,
    int SortOrder);

/// <summary>A technical corridor or piece of street furniture along a zone.</summary>
public sealed record StreetFeatureRow(
    int FeatureId,
    string FeatureType,
    string Label,
    decimal Latitude,
    decimal Longitude,
    bool BlocksBusiness,
    string? Note);

/// <summary>A registration's hold on one slot. Expired rows may still exist; callers compare ExpiresAt.</summary>
public sealed record SlotHoldRow(
    long SlotId,
    long RegistrationId,
    DateTime HeldAt,
    DateTime ExpiresAt);
