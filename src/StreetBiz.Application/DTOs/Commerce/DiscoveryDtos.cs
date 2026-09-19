namespace StreetBiz.Application.DTOs.Commerce;

/// <summary>An opening window; times are local "HH:mm", <c>DayOfWeek</c> is ISO (1 = Monday ... 7 = Sunday).</summary>
public sealed record StorefrontHourDto(int DayOfWeek, string OpensAt, string ClosesAt);

public sealed record StorefrontSummaryDto(
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
    double? DistanceMeters,
    bool IsOpenNow,
    IReadOnlyList<StorefrontHourDto> TodayHours,
    decimal? CommunityRating,
    int CommunityCount,
    int MenuItemCount,
    decimal? MinPrice,
    IReadOnlyList<string> Categories);

public sealed record StorefrontMenuCategoryDto(
    int CategoryId,
    string CategoryName,
    IReadOnlyList<MarketplaceMenuItemDto> Items);

public sealed record StorefrontDetailDto(
    StorefrontSummaryDto Storefront,
    IReadOnlyList<StorefrontHourDto> WeeklyHours,
    IReadOnlyList<StorefrontMenuCategoryDto> Menu);

/// <summary>A ward that has at least one listed storefront. <c>DistanceMeters</c> is to its nearest one.</summary>
public sealed record ServiceAreaDto(
    int WardId,
    string WardName,
    string? DistrictName,
    int StorefrontCount,
    double? DistanceMeters);

public sealed record MarketplaceCategoryDto(int CategoryId, string CategoryName, int ItemCount);
