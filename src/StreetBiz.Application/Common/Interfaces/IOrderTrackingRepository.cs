using StreetBiz.Application.Common.Models;

namespace StreetBiz.Application.Common.Interfaces;

/// <summary>Read-only queries behind order tracking (ORD-02) and the pickup-range check (ORD-01).</summary>
public interface IOrderTrackingRepository
{
    /// <summary>
    /// The stall a checkout is about to order from: the given cart's, or with no cart id (the
    /// legacy <c>POST /api/orders</c>) the customer's newest active cart's. The same cart the order
    /// itself is then created from. Null when there is no such active cart.
    /// </summary>
    Task<PickupPointRow?> GetCheckoutPickupPointAsync(
        long customerUserId,
        long? cartId,
        CancellationToken cancellationToken);

    /// <summary>Any storefront's pickup point, open or not; null when it does not exist.</summary>
    Task<PickupPointRow?> GetStorefrontPickupPointAsync(
        long storefrontId,
        CancellationToken cancellationToken);

    /// <summary>Null when the order does not exist or belongs to another customer.</summary>
    Task<OrderTrackingRow?> GetOrderTrackingAsync(
        long customerUserId,
        long orderId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Accepted-to-ready durations, in minutes, of the storefront's most recent orders that were
    /// marked ready since <paramref name="sinceUtc"/>, newest first, at most <paramref name="take"/>.
    /// </summary>
    Task<IReadOnlyList<double>> GetRecentPrepMinutesAsync(
        long storefrontId,
        DateTime sinceUtc,
        int take,
        CancellationToken cancellationToken);

    /// <summary>Null when the order does not exist or belongs to another customer.</summary>
    Task<ArrivalTargetRow?> GetArrivalTargetAsync(long customerUserId, long orderId, CancellationToken cancellationToken);

    /// <summary>Notifies the stall's owner that the customer is on the way (an in-app notification).</summary>
    Task RecordArrivalAsync(
        long orderId, long vendorUserId, string title, string body, DateTime now, CancellationToken cancellationToken);

    /// <summary>The latest arrival notice per still-collectable order of the vendor's stalls since <paramref name="sinceUtc"/>.</summary>
    Task<IReadOnlyList<OrderArrivalRow>> ListVendorArrivalsAsync(
        long vendorId, DateTime sinceUtc, CancellationToken cancellationToken);
}
