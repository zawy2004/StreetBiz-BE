namespace StreetBiz.Application.Features.Commerce;

public sealed record StorefrontDto(long StorefrontId, long RegistrationId, long ContractId,
    string Name, string? Description, string AvailabilityStatus);
public sealed record StorefrontInput(long RegistrationId, long ContractId, string Name,
    string? Description, string AvailabilityStatus);
public sealed record SellerMenuItemDto(long MenuItemId, long StorefrontId, int CategoryId,
    string Name, string? Description, decimal UnitPrice, string AvailabilityStatus);
public sealed record SellerMenuInput(int CategoryId, string Name, string? Description,
    decimal UnitPrice, string AvailabilityStatus);
public sealed record SellerCategoryDto(int CategoryId, string Name);
public sealed record CustomerComplaintDto(long ComplaintId, long OrderId, string ComplaintType,
    string Description, decimal? RequestedRefundAmount, string Status, string? ResolutionNotes, DateTime CreatedAt);
public sealed record CustomerComplaintInput(string ComplaintType, string Description, decimal? RequestedRefundAmount);
public sealed record OrderReviewDto(long ReviewId, short Rating, string? Text);
public sealed record OrderReviewInput(short Rating, string? Text);

public interface ICommerceManagement
{
    Task<IReadOnlyList<StorefrontDto>> Stores(CancellationToken ct);
    Task<StorefrontDto> SaveStore(long? id, StorefrontInput input, CancellationToken ct);
    Task<IReadOnlyList<SellerCategoryDto>> Categories(CancellationToken ct);
    Task<IReadOnlyList<SellerMenuItemDto>> Menu(long storeId, CancellationToken ct);
    Task<SellerMenuItemDto> SaveMenu(long storeId, long? itemId, SellerMenuInput input, CancellationToken ct);
    Task ArchiveMenu(long storeId, long itemId, CancellationToken ct);
    Task<IReadOnlyList<CustomerComplaintDto>> Complaints(long orderId, CancellationToken ct);
    Task<CustomerComplaintDto> Complain(long orderId, CustomerComplaintInput input, CancellationToken ct);
    Task<OrderReviewDto?> Review(long orderId, CancellationToken ct);
    Task<OrderReviewDto> SaveReview(long orderId, OrderReviewInput input, CancellationToken ct);
}
