using StreetBiz.Application.Common.Models;

namespace StreetBiz.Application.Common.Interfaces;

public interface ICommerceRepository
{
    Task<IReadOnlyList<MarketplaceMenuItemRow>> SearchMenuItemsAsync(
        string? query,
        int take,
        CancellationToken cancellationToken);

    Task<MarketplaceMenuItemRow?> GetMenuItemAsync(
        long menuItemId,
        CancellationToken cancellationToken);

    Task<CommerceCartRow?> GetActiveCartAsync(
        long customerUserId,
        CancellationToken cancellationToken);

    Task<CartMutationResult> AddCartItemAsync(
        long customerUserId,
        long menuItemId,
        int quantity,
        string? note,
        CancellationToken cancellationToken);

    Task<CartMutationResult> UpdateCartItemAsync(
        long customerUserId,
        long menuItemId,
        int quantity,
        string? note,
        CancellationToken cancellationToken);

    Task<CartMutationResult> RemoveCartItemAsync(
        long customerUserId,
        long menuItemId,
        CancellationToken cancellationToken);

    Task ClearCartAsync(long customerUserId, CancellationToken cancellationToken);

    Task<OrderMutationResult> PlacePrepaidOrderAsync(
        long customerUserId,
        string provider,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task<OrderMutationResult> ConfirmSandboxPaymentAsync(
        long customerUserId,
        long orderId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CommerceOrderRow>> ListCustomerOrdersAsync(
        long customerUserId,
        CancellationToken cancellationToken);

    Task<CommerceOrderRow?> GetCustomerOrderAsync(
        long customerUserId,
        long orderId,
        CancellationToken cancellationToken);

    Task<OrderMutationResult> CancelCustomerOrderAsync(
        long customerUserId,
        long orderId,
        string expectedStatus,
        CancellationToken cancellationToken);

    Task<OrderMutationResult> ConfirmCustomerPickupAsync(
        long customerUserId,
        long orderId,
        string expectedStatus,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CommerceOrderRow>> ListSellerOrdersAsync(
        long vendorId,
        string? status,
        CancellationToken cancellationToken);

    Task<CommerceOrderRow?> GetSellerOrderAsync(
        long vendorId,
        long orderId,
        CancellationToken cancellationToken);

    Task<OrderMutationResult> DecideSellerOrderAsync(
        long vendorId,
        long actorUserId,
        long orderId,
        string decision,
        string? reason,
        string expectedStatus,
        CancellationToken cancellationToken);

    Task<OrderMutationResult> UpdateSellerOrderStatusAsync(
        long vendorId,
        long actorUserId,
        long orderId,
        string targetStatus,
        string expectedStatus,
        CancellationToken cancellationToken);

    Task<OrderMutationResult> ConfirmSellerHandoverAsync(
        long vendorId,
        long actorUserId,
        long orderId,
        string expectedStatus,
        CancellationToken cancellationToken);

    Task<CommerceSalesSummaryRow> GetSalesSummaryAsync(
        long vendorId,
        string period,
        CancellationToken cancellationToken);
}
