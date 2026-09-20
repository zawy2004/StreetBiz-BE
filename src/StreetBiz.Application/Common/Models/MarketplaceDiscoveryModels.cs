using StreetBiz.Application.Common.Geo;

namespace StreetBiz.Application.Common.Models;

/// <summary>One opening window of a storefront. <c>DayOfWeek</c> is ISO: 1 = Monday ... 7 = Sunday.</summary>
public sealed record StorefrontHourRow(short DayOfWeek, TimeOnly OpensAt, TimeOnly ClosesAt);

/// <summary>A local (Vietnam) weekday and wall-clock time, used to decide "open now".</summary>
public sealed record MarketplaceOpenAt(short DayOfWeek, TimeOnly Time);

public sealed record MarketplaceStorefrontFilter(
    string? Query,
    int? WardId,
    int? CategoryId,
    MarketplaceOpenAt? OpenAt,
    GeoBoundingBox? Area);

public sealed record MarketplaceMenuFilter(
    string? Query,
    int? WardId,
    int? CategoryId,
    decimal? MinPrice,
    decimal? MaxPrice,
    MarketplaceOpenAt? OpenAt,
    string Sort);

public sealed record MarketplaceStorefrontRow(
    long StorefrontId,
    string StorefrontName,
    string? Description,
    string? ImageUrl,
    long VendorId,
    string? Address,
    int WardId,
    string WardName,
    string ZoneName,
    string SlotCode,
    decimal Latitude,
    decimal Longitude,
    decimal? CommunityRating,
    int CommunityCount,
    int MenuItemCount,
    decimal? MinPrice,
    IReadOnlyList<string> Categories,
    IReadOnlyList<StorefrontHourRow> Hours);

public sealed record MarketplaceStorefrontDetailRow(
    MarketplaceStorefrontRow Storefront,
    IReadOnlyList<MarketplaceMenuItemRow> Items);

public sealed record MarketplaceCategoryRow(int CategoryId, string CategoryName, int ItemCount);

/// <summary>Where one publicly listed storefront stands, so wards can be counted and ranked by distance.</summary>
public sealed record StorefrontLocationRow(
    int WardId,
    string WardName,
    string? DistrictName,
    decimal Latitude,
    decimal Longitude);
