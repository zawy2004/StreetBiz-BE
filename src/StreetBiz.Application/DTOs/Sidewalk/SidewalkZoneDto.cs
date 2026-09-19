using StreetBiz.Application.Common.Models;

namespace StreetBiz.Application.DTOs.Sidewalk;

public sealed record SidewalkZoneDto(
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
    IReadOnlyList<FeeComponentDto> FeeComponents,
    IReadOnlyList<StreetFeatureDto> Features);

public sealed record FeeComponentDto(int ComponentId, string ComponentName, string CalcBasis, decimal UnitAmount);

public sealed record StreetFeatureDto(
    int FeatureId,
    string FeatureType,
    string Label,
    decimal Latitude,
    decimal Longitude,
    bool BlocksBusiness,
    string? Note);

/// <summary>Maps domain records to DTOs for the Sidewalk Zone API.</summary>
public static class ZoneMapper
{
    public static SidewalkZoneDto ToDto(this ZoneDetailRow r) => new(
        r.ZoneId, r.ZoneName, r.ZoneCode, r.RegulationRef, r.SegmentFrom, r.SegmentTo, r.ApplicationDeadline,
        r.WardUnitId, r.WardName, r.ContactName, r.ContactPhone, r.PricePerDay, r.AvailableFrom, r.AvailableTo,
        r.FeeComponents.Select(c => new FeeComponentDto(c.ComponentId, c.ComponentName, c.CalcBasis, c.UnitAmount)).ToList(),
        r.Features.Select(f => new StreetFeatureDto(
            f.FeatureId, f.FeatureType, f.Label, f.Latitude, f.Longitude, f.BlocksBusiness, f.Note)).ToList());
}

/// <summary>A live hold. <c>ExpiresAt</c> is UTC; the client derives the countdown from it.</summary>
public sealed record SlotHoldDto(long SlotId, long RegistrationId, DateTime HeldAt, DateTime ExpiresAt);

public static class SlotHoldMapper
{
    // The database stores UTC in a datetime2 column, so EF hands back Kind=Unspecified; without
    // marking it UTC the JSON has no "Z" and a browser would read the countdown as local time.
    public static SlotHoldDto ToDto(this SlotHoldRow r) => new(
        r.SlotId, r.RegistrationId,
        DateTime.SpecifyKind(r.HeldAt, DateTimeKind.Utc), DateTime.SpecifyKind(r.ExpiresAt, DateTimeKind.Utc));
}

/// <summary>One priced line of a quote. <c>Kind</c> is RENT (price_per_day x days) or FEE (a zone fee component).</summary>
public sealed record FeeQuoteLineDto(string Kind, string? Label, string CalcBasis, decimal UnitAmount, int Quantity, decimal Amount);

public sealed record FeeQuoteDto(long SlotId, int TermDays, IReadOnlyList<FeeQuoteLineDto> Lines, decimal Total);
