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
    TimeOnly? AvailableTo);

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
