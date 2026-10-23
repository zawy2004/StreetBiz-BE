using StreetBiz.Application.DTOs.Community;

namespace StreetBiz.Application.Features.Commerce;

public sealed record StorefrontDto(long StorefrontId, long RegistrationId, long ContractId,
    string Name, string? Description, string AvailabilityStatus);
public sealed record StorefrontInput(long RegistrationId, long ContractId, string Name,
    string? Description, string AvailabilityStatus);
/// <param name="FoodSafetyStatus">One of <c>DishFoodSafetyStatuses</c>: whether this dish needs
/// an ATTP certificate and where it stands. MISSING/PENDING dishes are off sale.</param>
/// <param name="PriceUnit">The unit the price is for ("ổ", "ly", "kg"...), as on the price board.</param>
public sealed record SellerMenuItemDto(long MenuItemId, long StorefrontId, int CategoryId,
    string Name, string? Description, decimal UnitPrice, string AvailabilityStatus,
    string? ImageUrl = null, string CategoryName = "", bool RequiresFoodSafety = false,
    string FoodSafetyStatus = "NOT_REQUIRED", DateOnly? FoodSafetyExpiresOn = null,
    string? NameEn = null, string PriceUnit = PriceBoardRules.DefaultPriceUnit);
public sealed record SellerMenuDto(IReadOnlyList<SellerMenuItemDto> Items, int MaxItems);
/// <param name="ImageUrl">A URL returned by POST /api/uploads/menu-images; required for a new dish.</param>
/// <param name="PriceUnit">Required (Luật Giá 2023 Điều 29: a posted price names its unit), 1-30 characters.</param>
/// <param name="NameEn">Optional English name for the price board (D8: typed by the vendor).</param>
public sealed record SellerMenuInput(int CategoryId, string Name, string? Description,
    decimal UnitPrice, string AvailabilityStatus, string? ImageUrl = null,
    string? PriceUnit = null, string? NameEn = null);
/// <summary>
/// BUY-06: the stall's price board as the vendor manages it. <paramref name="Version"/> is "bản số
/// N" (the storefront's count of price-history rows); <paramref name="QrUrl"/> is what the printed
/// board's QR encodes (<c>/q/{token}?v=N</c>) and <paramref name="PrintUrl"/> the public A5 page.
/// Both are null without a live permit or without Permit:PublicScanBaseUrl.
/// </summary>
public sealed record SellerPriceBoardDto(long StorefrontId, string StorefrontName, string? SlotCode,
    int Version, DateTime? UpdatedAt, string? QrUrl, string? PrintUrl,
    IReadOnlyList<PriceBoardItemDto> Items, IReadOnlyList<PriceHistoryEntryDto> History,
    IReadOnlyList<PriceBoardPermitDto> Boards);

/// <summary>
/// One paper board to hang: a slot the business holds with a live permit, and the links its QR and
/// A5 page use. A business renting two slots prints two boards with the same prices and different
/// QR codes, because each QR is that slot's own signed permit. <see cref="SellerPriceBoardDto.QrUrl"/>
/// and <see cref="SellerPriceBoardDto.PrintUrl"/> are the first of these (the storefront's own slot).
/// </summary>
public sealed record PriceBoardPermitDto(long PermitId, long ContractId, string SlotCode, string? QrUrl, string? PrintUrl);
/// <param name="PreviousUnitPrice">The dish's price on its previous row; null for its first row.</param>
/// <param name="ChangedByName">The vendor's own name, or "Quản trị viên" for a moderator's hide/restore.</param>
public sealed record PriceHistoryEntryDto(long PriceHistoryId, long MenuItemId, string Name, string? NameEn,
    string PriceUnit, decimal UnitPrice, decimal? PreviousUnitPrice, bool IsListed, string ChangeKind,
    DateTime ChangedAt, string ChangedByName);

/// <summary>BUY-06 price board rules shared by the vendor's menu, moderation and the public board.</summary>
public static class PriceBoardRules
{
    public const string DefaultPriceUnit = "phần";
    public const int PriceUnitMaxLength = 30;
    public const int NameEnMaxLength = 180;
    /// <summary>The vendor's price-history pane shows this many latest changes.</summary>
    public const int HistoryTake = 50;

    public const string Created = "CREATED";
    public const string Updated = "UPDATED";
    public const string Archived = "ARCHIVED";
    public const string Restored = "RESTORED";

    /// <summary>A dish is on the price board while it can be bought or is only sold out today.</summary>
    public static bool IsListed(string availabilityStatus) => availabilityStatus is "AVAILABLE" or "SOLD_OUT";

    public static readonly IReadOnlyList<string> ListedStatuses = ["AVAILABLE", "SOLD_OUT"];
}
/// <param name="Hours">The weekly windows, ordered by day then opening time; empty means the shop is
/// governed only by its open/closed switch (STORE-04).</param>
/// <param name="AllowedWindow">The hours the ward rents the pitch for, or null when the zone sets none.</param>
public sealed record StorefrontHoursDto(long StorefrontId, IReadOnlyList<BusinessHourItem> Hours,
    RentalWindowDto? AllowedWindow, string AvailabilityStatus, bool IsOpenNow);
public sealed record RentalWindowDto(string From, string To, bool Overnight);
public sealed record StorefrontHoursInput(IReadOnlyList<BusinessHourItem>? Hours);
public sealed record SellerCategoryDto(int CategoryId, string Name, bool RequiresFoodSafety = false);
public sealed record CustomerComplaintDto(long ComplaintId, long OrderId, string ComplaintType,
    string Description, decimal? RequestedRefundAmount, string Status, string? ResolutionNotes, DateTime CreatedAt);
public sealed record CustomerComplaintInput(string ComplaintType, string Description, decimal? RequestedRefundAmount);
/// <param name="EditableUntil">UTC instant after which the review can no longer be edited (BR-68).</param>
public sealed record OrderReviewDto(long ReviewId, short Rating, string? Text, DateTime? EditableUntil = null);
public sealed record OrderReviewInput(short Rating, string? Text);

public interface ICommerceManagement
{
    Task<IReadOnlyList<StorefrontDto>> Stores(CancellationToken ct);
    Task<StorefrontDto> SaveStore(long? id, StorefrontInput input, CancellationToken ct);
    Task<StorefrontHoursDto> BusinessHours(long storeId, CancellationToken ct);
    Task<StorefrontHoursDto> SaveBusinessHours(long storeId, StorefrontHoursInput input, CancellationToken ct);
    Task<IReadOnlyList<SellerCategoryDto>> Categories(CancellationToken ct);
    Task<SellerMenuDto> Menu(long storeId, CancellationToken ct);
    Task<SellerMenuItemDto> SaveMenu(long storeId, long? itemId, SellerMenuInput input, CancellationToken ct);
    Task ArchiveMenu(long storeId, long itemId, CancellationToken ct);
    Task<SellerPriceBoardDto> PriceBoard(long storeId, CancellationToken ct);
    Task<IReadOnlyList<CustomerComplaintDto>> Complaints(long orderId, CancellationToken ct);
    Task<CustomerComplaintDto> Complain(long orderId, CustomerComplaintInput input, CancellationToken ct);
    Task<OrderReviewDto?> Review(long orderId, CancellationToken ct);
    Task<OrderReviewDto> SaveReview(long orderId, OrderReviewInput input, CancellationToken ct);
}
