namespace StreetBiz.Application.Common.Models;

/// <summary>A geocoded point, rounded to match the DECIMAL(9,6) columns it is written into.</summary>
public sealed record GeoPoint(decimal Latitude, decimal Longitude, string? DisplayName);
