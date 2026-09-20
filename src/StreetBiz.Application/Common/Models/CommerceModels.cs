namespace StreetBiz.Application.Common.Models;

public sealed record MarketplaceMenuItemRow(
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

public sealed record CommerceCartItemRow(
    long CartItemId,
    long MenuItemId,
    string ItemName,
    string? ImageUrl,
    decimal UnitPrice,
    string AvailabilityStatus,
    int Quantity,
    string? Note);

public sealed record CommerceCartRow(
    long CartId,
    long StorefrontId,
    string StorefrontName,
    string StorefrontStatus,
    IReadOnlyList<CommerceCartItemRow> Items,
    decimal Subtotal,
    string? StorefrontAddress = null);

public sealed record CommerceOrderItemRow(
    long OrderItemId,
    long MenuItemId,
    string ItemName,
    decimal UnitPrice,
    int Quantity,
    string? Note);

public sealed record CommerceOrderHistoryRow(
    long HistoryId,
    string? FromStatus,
    string ToStatus,
    string? Note,
    DateTime ChangedAt);

public sealed record CommerceOrderRow(
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
    IReadOnlyList<CommerceOrderItemRow> Items,
    IReadOnlyList<CommerceOrderHistoryRow> History,
    long? PaymentTransactionId = null,
    string? PaymentIdempotencyKey = null,
    decimal? PaymentAmount = null,
    string? PaymentProviderReference = null,
    string? StorefrontImageUrl = null,
    string? StorefrontAddress = null);

public sealed record PaymentCallbackData(
    string Provider,
    string? ProviderReference,
    string? IdempotencyKey,
    decimal? Amount,
    string? Status,
    string RawPayload,
    bool SignatureValid);

public enum PaymentCallbackOutcome
{
    Applied,
    Duplicate,
    Unmatched,
    Rejected,
}

public sealed record PaymentCallbackMutationResult(
    PaymentCallbackOutcome Outcome,
    long CallbackEventId,
    long? TransactionId,
    long? OrderId,
    string? OrderStatus);

public sealed record CommerceSalesSummaryRow(
    string Period,
    DateTime FromUtc,
    DateTime ToUtc,
    int CompletedOrderCount,
    decimal GrossSales,
    decimal RefundedAmount,
    decimal NetSales,
    IReadOnlyList<CommerceOrderRow> Orders);

public enum CartMutationOutcome
{
    Updated,
    NotFound,
    MenuItemUnavailable,
    StorefrontUnavailable,
    Conflict,
}

public sealed record CartMutationResult(CartMutationOutcome Outcome, CommerceCartRow? Cart);

public enum OrderMutationOutcome
{
    Updated,
    NotFound,
    Conflict,
    EmptyCart,
    MenuItemUnavailable,
    StorefrontUnavailable,
    PaymentNotFound,
}

public sealed record OrderMutationResult(OrderMutationOutcome Outcome, CommerceOrderRow? Order);
