namespace StreetBiz.Application.Features.Commerce;

public sealed record StorefrontDto(long StorefrontId, long RegistrationId, long ContractId,
    string Name, string? Description, string AvailabilityStatus);
public sealed record StorefrontInput(long RegistrationId, long ContractId, string Name,
    string? Description, string AvailabilityStatus);
/// <param name="FoodSafetyStatus">One of <c>DishFoodSafetyStatuses</c>: whether this dish needs
/// an ATTP certificate and where it stands. MISSING/PENDING dishes are off sale.</param>
public sealed record SellerMenuItemDto(long MenuItemId, long StorefrontId, int CategoryId,
    string Name, string? Description, decimal UnitPrice, string AvailabilityStatus,
    string? ImageUrl = null, string CategoryName = "", bool RequiresFoodSafety = false,
    string FoodSafetyStatus = "NOT_REQUIRED", DateOnly? FoodSafetyExpiresOn = null);
public sealed record SellerMenuDto(IReadOnlyList<SellerMenuItemDto> Items, int MaxItems);
/// <param name="ImageUrl">A URL returned by POST /api/uploads/menu-images; required for a new dish.</param>
public sealed record SellerMenuInput(int CategoryId, string Name, string? Description,
    decimal UnitPrice, string AvailabilityStatus, string? ImageUrl = null);
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
    Task<IReadOnlyList<CustomerComplaintDto>> Complaints(long orderId, CancellationToken ct);
    Task<CustomerComplaintDto> Complain(long orderId, CustomerComplaintInput input, CancellationToken ct);
    Task<OrderReviewDto?> Review(long orderId, CancellationToken ct);
    Task<OrderReviewDto> SaveReview(long orderId, OrderReviewInput input, CancellationToken ct);
}
