namespace StreetBiz.Application.DTOs.Commerce;

public sealed record MarketplaceMenuItemDto(
    long MenuItemId,
    long StorefrontId,
    string StorefrontName,
    string ItemName,
    string? Description,
    string? ImageUrl,
    decimal UnitPrice,
    string AvailabilityStatus,
    int CategoryId,
    string CategoryName);

public sealed record CartItemDto(
    long CartItemId,
    long MenuItemId,
    string ItemName,
    string? ImageUrl,
    decimal UnitPrice,
    string AvailabilityStatus,
    int Quantity,
    string? Note);

public sealed record CartDto(
    long CartId,
    long StorefrontId,
    string StorefrontName,
    string StorefrontStatus,
    IReadOnlyList<CartItemDto> Items,
    decimal Subtotal)
{
    public string? StorefrontAddress { get; init; }
}

public sealed record OrderItemDto(
    long OrderItemId,
    long MenuItemId,
    string ItemName,
    decimal UnitPrice,
    int Quantity,
    string? Note)
{
    public decimal LineTotal => UnitPrice * Quantity;
}

public sealed record OrderHistoryDto(
    long HistoryId,
    string? FromStatus,
    string ToStatus,
    string? Note,
    DateTime ChangedAt);

public sealed record OrderStorefrontDto(
    long StorefrontId,
    string StorefrontName,
    string? ImageUrl,
    string? Address);

public sealed record OrderDto(
    long OrderId,
    string OrderCode,
    long CustomerUserId,
    string CustomerName,
    long StorefrontId,
    string StorefrontName,
    string OrderStatus,
    decimal SubtotalAmount,
    decimal TotalAmount,
    string? RejectionReason,
    string? PaymentProvider,
    string? PaymentStatus,
    decimal? RefundAmount,
    string? RefundReason,
    string? RefundStatus,
    DateTime? RefundRequestedAt,
    DateTime? RefundCompletedAt,
    DateTime? PlacedAt,
    DateTime? CompletedAt,
    DateTime CreatedAt,
    IReadOnlyList<OrderItemDto> Items,
    IReadOnlyList<OrderHistoryDto> History)
{
    public string? StorefrontImageUrl { get; init; }
    public string? StorefrontAddress { get; init; }
    public OrderStorefrontDto Storefront => new(
        StorefrontId, StorefrontName, StorefrontImageUrl, StorefrontAddress);
    public IReadOnlyList<OrderHistoryDto> StatusHistory => History;
}

public sealed record CheckoutPaymentDto(
    long TransactionId,
    string Provider,
    decimal Amount,
    string Status,
    string PaymentUrl);

public sealed record CheckoutDto(
    long OrderId,
    string OrderCode,
    string OrderStatus,
    long PaymentTransactionId,
    string Provider,
    decimal Amount,
    string PaymentUrl);

public sealed record PaymentCallbackReceiptDto(
    string Result,
    long CallbackEventId,
    long? TransactionId,
    long? OrderId,
    string? OrderStatus);

public sealed record PagedResultDto<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalItems,
    int TotalPages);

public sealed record SalesBucketDto(
    string Key,
    int CompletedOrderCount,
    decimal GrossSales,
    decimal RefundedAmount,
    decimal NetSales);

public sealed record DetailedSalesSummaryDto(
    DateTime FromDate,
    DateTime ToDate,
    string GroupBy,
    int CompletedOrderCount,
    decimal GrossSales,
    decimal RefundedAmount,
    decimal NetSales,
    IReadOnlyList<SalesBucketDto> Buckets);

public sealed record SalesSummaryDto(
    string Period,
    DateTime FromUtc,
    DateTime ToUtc,
    int CompletedOrderCount,
    decimal GrossSales,
    decimal RefundedAmount,
    decimal NetSales,
    IReadOnlyList<OrderDto> Orders);
