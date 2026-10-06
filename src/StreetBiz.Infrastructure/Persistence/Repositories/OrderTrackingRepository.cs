using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.Infrastructure.Persistence.Repositories;

/// <summary>
/// ORD-01 pickup range and ORD-02 tracking. Read-only, and separate from CommerceRepository: it
/// adds questions about orders without touching how orders are written.
/// </summary>
public sealed class OrderTrackingRepository(StreetBizDbContext db) : IOrderTrackingRepository
{
    public async Task<PickupPointRow?> GetCheckoutPickupPointAsync(
        long customerUserId,
        long? cartId,
        CancellationToken cancellationToken)
    {
        // Same cart choice as CommerceRepository.CreatePrepaidOrderAsync, so the range is checked
        // against the stall the order is then actually placed with.
        var point = await db.ShoppingCarts.AsNoTracking()
            .Where(cart => cart.customer_user_id == customerUserId
                && cart.cart_status == CartStatuses.Active
                && (!cartId.HasValue || cart.cart_id == cartId.Value))
            .OrderByDescending(cart => cart.created_at)
            .ThenByDescending(cart => cart.cart_id)
            .Select(cart => new PointData(
                cart.storefront_id,
                cart.storefront.storefront_name,
                cart.storefront.registration.declared_address,
                cart.storefront.contract.slot.latitude,
                cart.storefront.contract.slot.longitude))
            .FirstOrDefaultAsync(cancellationToken);
        return point?.ToRow();
    }

    public async Task<PickupPointRow?> GetStorefrontPickupPointAsync(
        long storefrontId,
        CancellationToken cancellationToken)
    {
        var point = await db.Storefronts.AsNoTracking()
            .Where(storefront => storefront.storefront_id == storefrontId)
            .Select(storefront => new PointData(
                storefront.storefront_id,
                storefront.storefront_name,
                storefront.registration.declared_address,
                storefront.contract.slot.latitude,
                storefront.contract.slot.longitude))
            .SingleOrDefaultAsync(cancellationToken);
        return point?.ToRow();
    }

    public async Task<OrderTrackingRow?> GetOrderTrackingAsync(
        long customerUserId,
        long orderId,
        CancellationToken cancellationToken)
    {
        var order = await db.Orders.AsNoTracking()
            .Where(row => row.order_id == orderId && row.customer_user_id == customerUserId)
            .Select(row => new
            {
                row.order_status,
                row.placed_at,
                // The address as it read when the order was placed, like the order itself shows.
                Point = new PointData(
                    row.storefront_id,
                    row.storefront.storefront_name,
                    row.storefront_address_snapshot ?? row.storefront.registration.declared_address,
                    row.storefront.contract.slot.latitude,
                    row.storefront.contract.slot.longitude),
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (order is null)
        {
            return null;
        }

        var milestones = await db.OrderStatusHistories.AsNoTracking()
            .Where(history => history.order_id == orderId
                && (history.to_status == OrderStatuses.Accepted
                    || history.to_status == OrderStatuses.ReadyForPickup))
            .Select(history => new { history.to_status, history.changed_at })
            .ToListAsync(cancellationToken);
        DateTime? FirstAt(string status) => Utc(milestones
            .Where(history => history.to_status == status)
            .Select(history => (DateTime?)history.changed_at)
            .Min());

        return new OrderTrackingRow(
            orderId,
            order.order_status,
            Utc(order.placed_at),
            FirstAt(OrderStatuses.Accepted),
            FirstAt(OrderStatuses.ReadyForPickup),
            order.Point.ToRow(),
            await CountOrdersAheadAsync(order.Point.StorefrontId, orderId, order.order_status, order.placed_at, cancellationToken))
        {
            ArrivalNotifiedAt = Utc(await LastArrivalAsync(orderId, cancellationToken)),
        };
    }

    /// <summary>How an arrival notice is recognised among the seller's notifications.</summary>
    public const string ArrivalEntityType = "OrderArrival";

    public async Task<ArrivalTargetRow?> GetArrivalTargetAsync(
        long customerUserId, long orderId, CancellationToken cancellationToken)
    {
        var order = await db.Orders.AsNoTracking()
            .Where(row => row.order_id == orderId && row.customer_user_id == customerUserId)
            .Select(row => new
            {
                row.order_code,
                row.order_status,
                VendorUserId = row.storefront.registration.vendor.user_id,
            })
            .SingleOrDefaultAsync(cancellationToken);
        return order is null
            ? null
            : new ArrivalTargetRow(
                orderId, order.order_code, order.order_status, order.VendorUserId,
                await LastArrivalAsync(orderId, cancellationToken));
    }

    public async Task RecordArrivalAsync(
        long orderId, long vendorUserId, string title, string body, DateTime now, CancellationToken cancellationToken)
    {
        db.Notifications.Add(new ScaffoldedModels.Notification
        {
            user_id = vendorUserId,
            notification_type = "ORDER",
            title = title,
            body = body,
            related_entity_type = ArrivalEntityType,
            related_entity_id = orderId,
            sent_at = now,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<OrderArrivalRow>> ListVendorArrivalsAsync(
        long vendorId, DateTime sinceUtc, CancellationToken cancellationToken)
    {
        var vendorUserId = await db.Vendors.AsNoTracking()
            .Where(vendor => vendor.vendor_id == vendorId)
            .Select(vendor => (long?)vendor.user_id)
            .SingleOrDefaultAsync(cancellationToken);
        if (vendorUserId is null)
        {
            return [];
        }

        var notices = await db.Notifications.AsNoTracking()
            .Where(notification => notification.user_id == vendorUserId
                && notification.related_entity_type == ArrivalEntityType
                && notification.related_entity_id != null
                && notification.sent_at >= sinceUtc)
            .Select(notification => new
            {
                OrderId = notification.related_entity_id!.Value,
                notification.sent_at,
                notification.body,
            })
            .ToListAsync(cancellationToken);
        if (notices.Count == 0)
        {
            return [];
        }

        // Only orders of this vendor's stalls that are still to be handed over.
        var ids = notices.Select(notice => notice.OrderId).Distinct().ToArray();
        var open = await db.Orders.AsNoTracking()
            .Where(order => ids.Contains(order.order_id)
                && order.storefront.registration.vendor_id == vendorId
                && (order.order_status == OrderStatuses.Placed
                    || order.order_status == OrderStatuses.Accepted
                    || order.order_status == OrderStatuses.Preparing
                    || order.order_status == OrderStatuses.ReadyForPickup))
            .ToDictionaryAsync(order => order.order_id, order => order.order_code, cancellationToken);

        return notices
            .Where(notice => open.ContainsKey(notice.OrderId))
            .GroupBy(notice => notice.OrderId)
            .Select(group => group.OrderByDescending(notice => notice.sent_at).First())
            .OrderByDescending(notice => notice.sent_at)
            .Select(notice => new OrderArrivalRow(notice.OrderId, open[notice.OrderId], notice.sent_at, notice.body))
            .ToList();
    }

    private Task<DateTime?> LastArrivalAsync(long orderId, CancellationToken cancellationToken) =>
        db.Notifications.AsNoTracking()
            .Where(notification => notification.related_entity_type == ArrivalEntityType
                && notification.related_entity_id == orderId)
            .MaxAsync(notification => (DateTime?)notification.sent_at, cancellationToken);

    /// <summary>
    /// The schema stores every timestamp in UTC, but SQL Server hands DATETIME2 back unlabelled; a
    /// browser would read an unlabelled instant as local time, 7 hours off. Same as Commerce's mapping.
    /// </summary>
    private static DateTime? Utc(DateTime? value) =>
        value is null ? null : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);

    public async Task<IReadOnlyList<double>> GetRecentPrepMinutesAsync(
        long storefrontId,
        DateTime sinceUtc,
        int take,
        CancellationToken cancellationToken)
    {
        if (take <= 0)
        {
            return [];
        }

        var rows = await db.OrderStatusHistories.AsNoTracking()
            .Where(ready => ready.to_status == OrderStatuses.ReadyForPickup
                && ready.changed_at >= sinceUtc
                && ready.order.storefront_id == storefrontId)
            .Select(ready => new
            {
                ready.order_id,
                ReadyAt = ready.changed_at,
                AcceptedAt = db.OrderStatusHistories
                    .Where(accepted => accepted.order_id == ready.order_id
                        && accepted.to_status == OrderStatuses.Accepted)
                    .Min(accepted => (DateTime?)accepted.changed_at),
            })
            .Where(row => row.AcceptedAt != null)
            .OrderByDescending(row => row.ReadyAt)
            .ThenByDescending(row => row.order_id)
            .Take(take)
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => (row.ReadyAt - row.AcceptedAt!.Value).TotalMinutes)
            .ToArray();
    }

    /// <summary>
    /// Paid orders at the same stall that were placed earlier and are not ready yet: the queue in
    /// front of this one. Ties on placed_at fall back to the order id, so two orders never both
    /// count each other.
    /// </summary>
    private Task<int> CountOrdersAheadAsync(
        long storefrontId,
        long orderId,
        string status,
        DateTime? placedAt,
        CancellationToken cancellationToken)
    {
        if (placedAt is null
            || status is not (OrderStatuses.Placed or OrderStatuses.Accepted or OrderStatuses.Preparing))
        {
            return Task.FromResult(0);
        }

        var placed = placedAt.Value;
        return db.Orders.AsNoTracking()
            .Where(row => row.storefront_id == storefrontId
                && row.order_id != orderId
                && (row.order_status == OrderStatuses.Placed
                    || row.order_status == OrderStatuses.Accepted
                    || row.order_status == OrderStatuses.Preparing)
                && row.placed_at != null
                && (row.placed_at < placed || (row.placed_at == placed && row.order_id < orderId)))
            .CountAsync(cancellationToken);
    }

    // Decimals leave the query as-is and become doubles here: casting inside the query would
    // translate differently on SQL Server and on the SQLite the tests run against.
    private sealed record PointData(
        long StorefrontId,
        string StorefrontName,
        string? Address,
        decimal Latitude,
        decimal Longitude)
    {
        public PickupPointRow ToRow() => new(
            StorefrontId, StorefrontName, Address, (double)Latitude, (double)Longitude);
    }
}
