using StreetBiz.Application.Common.Models;

namespace StreetBiz.Application.DTOs.Sidewalk;

public sealed record SidewalkSlotDto(
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
    double? DistanceMeters);

/// <summary>Maps domain records to DTOs for the Sidewalk Slot API.</summary>
public static class SlotMapper
{
    public static SidewalkSlotDto ToDto(this SlotRow r, double? distanceMeters) => new(
        r.SlotId, r.SlotCode, r.ZoneId, r.ZoneName, r.WardUnitId,
        r.Latitude, r.Longitude, r.WidthMeters, r.LengthMeters,
        r.SlotStatus, r.Source, r.PricePerDay, r.AvailableFrom, r.AvailableTo,
        distanceMeters);
}

public sealed record SlotProposalDto(
    long SlotId,
    string SlotCode,
    decimal Latitude,
    decimal Longitude,
    string ProposalReviewStatus,
    string ProposalPhotoUrl,
    string? ProposalReviewReason,
    DateTime CreatedAt);

/// <summary>Maps domain records to DTOs for the Slot Proposal API (SIDE-11).</summary>
public static class SlotProposalMapper
{
    public static SlotProposalDto ToDto(this SlotProposalRow r) => new(
        r.SlotId, r.SlotCode, r.Latitude, r.Longitude,
        r.ProposalReviewStatus, r.ProposalPhotoUrl, r.ProposalReviewReason, r.CreatedAt);
}
