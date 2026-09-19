using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Persistence.Repositories;

public sealed class CommerceRepository(
    StreetBizDbContext db,
    TimeProvider clock) : ICommerceRepository
{
    private static readonly TimeZoneInfo BusinessTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");
    private const string StorefrontOpen = "OPEN";
    private const string MenuAvailable = "AVAILABLE";
    private const string MenuSoldOut = "SOLD_OUT";
    private const string PaymentPurposeOrder = "ORDER";
    private const string PaymentPending = "PENDING";
    private const string PaymentSuccess = "SUCCESS";
    private const string PaymentFailed = "FAILED";
    private const string RefundPending = "PENDING";
    private const string RefundSuccess = "SUCCESS";
    private const string RefundOrderCancelled = "ORDER_CANCELLED";
    private const string RefundOrderRejected = "ORDER_REJECTED";

    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(Now, BusinessTimeZone));

    private IQueryable<Storefront> EligibleStores() => db.Storefronts.Where(store =>
        store.availability_status == StorefrontOpen && store.registration.registration_status == "APPROVED"
        && store.contract.contract_status == "ACTIVE" && store.contract.start_date <= Today && store.contract.end_date >= Today
        && store.contract.vendor_id == store.registration.vendor_id
        && store.contract.application.registration_id == store.registration_id);

    public async Task<IReadOnlyList<MarketplaceMenuItemRow>> SearchMenuItemsAsync(
        string? query,
        int take,
        CancellationToken cancellationToken)
    {
        var items = MarketplaceMenuQuery();
        if (query is not null)
        {
            items = items.Where(item => item.item_name.Contains(query)
                || item.storefront.storefront_name.Contains(query)
                || item.category.category_name.Contains(query));
        }

        return await ProjectMarketplaceMenuItems(items
            .OrderBy(item => item.storefront.storefront_name)
            .ThenBy(item => item.item_name)
            .Take(take)
        ).ToListAsync(cancellationToken);
    }

    public Task<MarketplaceMenuItemRow?> GetMenuItemAsync(
        long menuItemId,
        CancellationToken cancellationToken) =>
        ProjectMarketplaceMenuItems(
            MarketplaceMenuQuery().Where(item => item.menu_item_id == menuItemId))
        .SingleOrDefaultAsync(cancellationToken);

    public async Task<CommerceCartRow?> GetActiveCartAsync(
        long customerUserId,
        CancellationToken cancellationToken)
    {
        var cartId = await db.ShoppingCarts.AsNoTracking()
            .Where(cart => cart.customer_user_id == customerUserId
                && cart.cart_status == CartStatuses.Active)
            .OrderByDescending(cart => cart.created_at)
            .ThenByDescending(cart => cart.cart_id)
            .Select(cart => (long?)cart.cart_id)
            .FirstOrDefaultAsync(cancellationToken);
        return cartId.HasValue ? await BuildCartAsync(cartId.Value, cancellationToken) : null;
    }

    public async Task<CartMutationResult> AddCartItemAsync(
        long customerUserId,
        long menuItemId,
        int quantity,
        string? note,
        CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        var result = await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken);
            var menuItem = await db.MenuItems.AsNoTracking()
                .Where(item => item.menu_item_id == menuItemId)
                .Select(item => new
                {
                    item.menu_item_id,
                    item.storefront_id,
                    item.availability_status,
                    StorefrontStatus = item.storefront.availability_status,
                })
                .SingleOrDefaultAsync(cancellationToken);
            if (menuItem is null)
            {
                return (CartMutationOutcome.NotFound, (long?)null);
            }

            if (menuItem.availability_status != MenuAvailable)
            {
                return (CartMutationOutcome.MenuItemUnavailable, (long?)null);
            }

            if (menuItem.StorefrontStatus != StorefrontOpen || !await EligibleStores().AnyAsync(x => x.storefront_id == menuItem.storefront_id, cancellationToken))
            {
                return (CartMutationOutcome.StorefrontUnavailable, (long?)null);
            }

            var otherCarts = await db.ShoppingCarts
                .Where(cart => cart.customer_user_id == customerUserId
                    && cart.cart_status == CartStatuses.Active
                    && cart.storefront_id != menuItem.storefront_id)
                .ToListAsync(cancellationToken);
            foreach (var oldCart in otherCarts)
            {
                oldCart.cart_status = CartStatuses.Abandoned;
            }

            var cart = await db.ShoppingCarts.SingleOrDefaultAsync(
                item => item.customer_user_id == customerUserId
                    && item.storefront_id == menuItem.storefront_id
                    && item.cart_status == CartStatuses.Active,
                cancellationToken);
            if (cart is null)
            {
                cart = new ShoppingCart
                {
                    customer_user_id = customerUserId,
                    storefront_id = menuItem.storefront_id,
                    cart_status = CartStatuses.Active,
                    created_at = Now,
                };
                db.ShoppingCarts.Add(cart);
                await db.SaveChangesAsync(cancellationToken);
            }

            var item = await db.ShoppingCartItems.SingleOrDefaultAsync(
                row => row.cart_id == cart.cart_id && row.menu_item_id == menuItemId,
                cancellationToken);
            if (item is null)
            {
                db.ShoppingCartItems.Add(new ShoppingCartItem
                {
                    cart_id = cart.cart_id,
                    menu_item_id = menuItemId,
                    quantity = quantity,
                    note = note,
                });
            }
            else
            {
                if (item.quantity + quantity > 99)
                {
                    return (CartMutationOutcome.Conflict, (long?)null);
                }

                item.quantity += quantity;
                item.note = note ?? item.note;
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (CartMutationOutcome.Updated, (long?)cart.cart_id);
        });

        var cartRow = result.Item1 == CartMutationOutcome.Updated && result.Item2.HasValue
            ? await BuildCartAsync(result.Item2.Value, cancellationToken)
            : null;
        return new CartMutationResult(result.Item1, cartRow);
    }

    public async Task<CartMutationResult> UpdateCartItemAsync(
        long customerUserId,
        long menuItemId,
        int quantity,
        string? note,
        CancellationToken cancellationToken)
    {
        var item = await db.ShoppingCartItems
            .Include(row => row.cart)
            .SingleOrDefaultAsync(row => row.menu_item_id == menuItemId
                && row.cart.customer_user_id == customerUserId
                && row.cart.cart_status == CartStatuses.Active,
                cancellationToken);
        if (item is null)
        {
            return new CartMutationResult(CartMutationOutcome.NotFound, null);
        }

        item.quantity = quantity;
        item.note = note;
        await db.SaveChangesAsync(cancellationToken);
        return new CartMutationResult(
            CartMutationOutcome.Updated,
            await BuildCartAsync(item.cart_id, cancellationToken));
    }

    public async Task<CartMutationResult> RemoveCartItemAsync(
        long customerUserId,
        long menuItemId,
        CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        var result = await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken);
            var item = await db.ShoppingCartItems
                .Include(row => row.cart)
                .SingleOrDefaultAsync(row => row.menu_item_id == menuItemId
                    && row.cart.customer_user_id == customerUserId
                    && row.cart.cart_status == CartStatuses.Active,
                    cancellationToken);
            if (item is null)
            {
                return (CartMutationOutcome.NotFound, (long?)null);
            }

            var cartId = item.cart_id;
            db.ShoppingCartItems.Remove(item);
            await db.SaveChangesAsync(cancellationToken);
            if (!await db.ShoppingCartItems.AnyAsync(
                    row => row.cart_id == cartId, cancellationToken))
            {
                item.cart.cart_status = CartStatuses.Abandoned;
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return (CartMutationOutcome.Updated, (long?)null);
            }

            await transaction.CommitAsync(cancellationToken);
            return (CartMutationOutcome.Updated, (long?)cartId);
        });

        return new CartMutationResult(
            result.Item1,
            result.Item2.HasValue
                ? await BuildCartAsync(result.Item2.Value, cancellationToken)
                : null);
    }

    public async Task ClearCartAsync(long customerUserId, CancellationToken cancellationToken)
    {
        var carts = await db.ShoppingCarts
            .Where(cart => cart.customer_user_id == customerUserId
                && cart.cart_status == CartStatuses.Active)
            .ToListAsync(cancellationToken);
        foreach (var cart in carts)
        {
            cart.cart_status = CartStatuses.Abandoned;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<OrderMutationResult> PlacePrepaidOrderAsync(
        long customerUserId,
        string provider,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        var result = await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken);
            var existing = await db.PaymentTransactions.AsNoTracking()
                .Where(payment => payment.idempotency_key == idempotencyKey)
                .Select(payment => new
                {
                    payment.order_id,
                    CustomerUserId = payment.order != null
                        ? payment.order.customer_user_id
                        : (long?)null,
                })
                .SingleOrDefaultAsync(cancellationToken);
            if (existing is not null)
            {
                if (existing.order_id.HasValue && existing.CustomerUserId == customerUserId)
                {
                    return (OrderMutationOutcome.Updated, existing.order_id);
                }

                return (OrderMutationOutcome.Conflict, (long?)null);
            }

            var cart = await db.ShoppingCarts
                .Include(row => row.storefront)
                .Include(row => row.ShoppingCartItems)
                    .ThenInclude(row => row.menu_item)
                .Where(row => row.customer_user_id == customerUserId
                    && row.cart_status == CartStatuses.Active)
                .OrderByDescending(row => row.created_at)
                .ThenByDescending(row => row.cart_id)
                .FirstOrDefaultAsync(cancellationToken);
            if (cart is null || cart.ShoppingCartItems.Count == 0)
            {
                return (OrderMutationOutcome.EmptyCart, (long?)null);
            }

            if (!await EligibleStores().AnyAsync(x => x.storefront_id == cart.storefront_id, cancellationToken))
            {
                return (OrderMutationOutcome.StorefrontUnavailable, (long?)null);
            }

            if (cart.ShoppingCartItems.Any(item =>
                    item.menu_item.storefront_id != cart.storefront_id
                    || item.menu_item.availability_status != MenuAvailable))
            {
                return (OrderMutationOutcome.MenuItemUnavailable, (long?)null);
            }

            var subtotal = cart.ShoppingCartItems.Sum(item =>
                item.menu_item.unit_price * item.quantity);
            var now = Now;
            var order = new Order
            {
                order_code = $"SB-{now:yyyyMMdd}-{Guid.NewGuid():N}"[..30],
                customer_user_id = customerUserId,
                storefront_id = cart.storefront_id,
                order_status = OrderStatuses.PendingPayment,
                subtotal_amount = subtotal,
                total_amount = subtotal,
                created_at = now,
            };
            foreach (var item in cart.ShoppingCartItems)
            {
                order.OrderItems.Add(new OrderItem
                {
                    menu_item_id = item.menu_item_id,
                    item_name_snapshot = item.menu_item.item_name,
                    unit_price_snapshot = item.menu_item.unit_price,
                    quantity = item.quantity,
                    note = item.note,
                });
            }

            order.OrderStatusHistories.Add(new OrderStatusHistory
            {
                from_status = null,
                to_status = OrderStatuses.PendingPayment,
                changed_by = customerUserId,
                changed_at = now,
            });
            order.PaymentTransactions.Add(new PaymentTransaction
            {
                idempotency_key = idempotencyKey,
                payment_purpose = PaymentPurposeOrder,
                provider = provider,
                amount = subtotal,
                transaction_status = PaymentPending,
                created_at = now,
            });
            cart.cart_status = CartStatuses.CheckedOut;
            db.Orders.Add(order);
            await db.SaveChangesAsync(cancellationToken);
            Audit(customerUserId, "ORD_PREPAID_CREATE", "Order", order.order_id,
                new { provider, idempotencyKey, subtotal });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (OrderMutationOutcome.Updated, (long?)order.order_id);
        });

        return await BuildOrderResultAsync(result, cancellationToken);
    }

    public async Task<OrderMutationResult> ConfirmSandboxPaymentAsync(
        long customerUserId,
        long orderId,
        CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        var result = await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken);
            var order = await db.Orders.SingleOrDefaultAsync(
                row => row.order_id == orderId && row.customer_user_id == customerUserId,
                cancellationToken);
            if (order is null)
            {
                return OrderMutationOutcome.NotFound;
            }

            var payment = await db.PaymentTransactions
                .Where(row => row.order_id == orderId && row.payment_purpose == PaymentPurposeOrder)
                .OrderByDescending(row => row.created_at)
                .ThenByDescending(row => row.transaction_id)
                .FirstOrDefaultAsync(cancellationToken);
            if (payment is null)
            {
                return OrderMutationOutcome.PaymentNotFound;
            }

            if (payment.transaction_status == PaymentSuccess
                && payment.provider_reference != null && payment.provider_reference.StartsWith("SANDBOX-"))
            {
                await transaction.CommitAsync(cancellationToken);
                return OrderMutationOutcome.Updated;
            }

            if ((payment.transaction_status != PaymentPending && payment.transaction_status != PaymentFailed)
                || (payment.provider_reference != null && !payment.provider_reference.StartsWith("SANDBOX-"))
                || order.order_status != OrderStatuses.PendingPayment)
            {
                return OrderMutationOutcome.Conflict;
            }

            if (!await EligibleStores().AnyAsync(x => x.storefront_id == order.storefront_id, cancellationToken))
                return OrderMutationOutcome.StorefrontUnavailable;

            var now = Now;
            payment.transaction_status = PaymentSuccess;
            payment.provider_reference = $"SANDBOX-{payment.provider}-{payment.transaction_id}";
            payment.callback_received_at = now;
            Transition(order, OrderStatuses.Placed, customerUserId, "Sandbox payment confirmed.", now);
            order.placed_at = now;
            var vendorUserId = await StorefrontVendorUserIdAsync(
                order.storefront_id, cancellationToken);
            Notify(vendorUserId, "ORDER", "Có đơn hàng mới",
                $"Đơn {order.order_code} đã thanh toán và đang chờ xác nhận.", "Order", orderId);
            Audit(customerUserId, "ORD_PAYMENT_SANDBOX_SUCCESS", "Order", orderId,
                new { payment.transaction_id, payment.provider });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return OrderMutationOutcome.Updated;
        });

        return result == OrderMutationOutcome.Updated
            ? new OrderMutationResult(result, await GetCustomerOrderAsync(
                customerUserId, orderId, cancellationToken))
            : new OrderMutationResult(result, null);
    }

    public async Task<IReadOnlyList<CommerceOrderRow>> ListCustomerOrdersAsync(
        long customerUserId,
        CancellationToken cancellationToken) =>
        await BuildOrdersAsync(
            db.Orders.AsNoTracking().Where(order => order.customer_user_id == customerUserId),
            cancellationToken);

    public async Task<CommerceOrderRow?> GetCustomerOrderAsync(
        long customerUserId,
        long orderId,
        CancellationToken cancellationToken) =>
        (await BuildOrdersAsync(
            db.Orders.AsNoTracking().Where(order => order.order_id == orderId
                && order.customer_user_id == customerUserId),
            cancellationToken)).SingleOrDefault();

    public async Task<OrderMutationResult> CancelCustomerOrderAsync(
        long customerUserId,
        long orderId,
        string expectedStatus,
        CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        var result = await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken);
            var order = await db.Orders.SingleOrDefaultAsync(
                row => row.order_id == orderId && row.customer_user_id == customerUserId,
                cancellationToken);
            if (order is null)
            {
                return OrderMutationOutcome.NotFound;
            }

            if (order.order_status != expectedStatus
                || order.order_status is not (OrderStatuses.PendingPayment or OrderStatuses.Placed))
            {
                return OrderMutationOutcome.Conflict;
            }

            if (order.order_status == OrderStatuses.Placed
                && !await AddFullRefundAsync(order, RefundOrderCancelled, cancellationToken))
            {
                return OrderMutationOutcome.PaymentNotFound;
            }

            if (order.order_status == OrderStatuses.PendingPayment)
            {
                var payments = await db.PaymentTransactions
                    .Where(payment => payment.order_id == orderId
                        && payment.transaction_status == PaymentPending)
                    .ToListAsync(cancellationToken);
                foreach (var payment in payments)
                {
                    payment.transaction_status = PaymentFailed;
                }
            }

            var now = Now;
            Transition(order, OrderStatuses.Cancelled, customerUserId, "Cancelled by customer.", now);
            order.completed_at = now;
            var vendorUserId = await StorefrontVendorUserIdAsync(
                order.storefront_id, cancellationToken);
            Notify(vendorUserId, "ORDER", "Đơn hàng đã bị huỷ",
                $"Khách hàng đã huỷ đơn {order.order_code}.", "Order", orderId);
            Audit(customerUserId, "ORD_CANCEL", "Order", orderId, new { expectedStatus });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return OrderMutationOutcome.Updated;
        });

        return result == OrderMutationOutcome.Updated
            ? new OrderMutationResult(result, await GetCustomerOrderAsync(
                customerUserId, orderId, cancellationToken))
            : new OrderMutationResult(result, null);
    }

    public Task<OrderMutationResult> ConfirmCustomerPickupAsync(
        long customerUserId,
        long orderId,
        string expectedStatus,
        CancellationToken cancellationToken) =>
        CompleteOrderAsync(customerUserId, null, orderId, expectedStatus, false, cancellationToken);

    public async Task<IReadOnlyList<CommerceOrderRow>> ListSellerOrdersAsync(
        long vendorId,
        string? status,
        CancellationToken cancellationToken)
    {
        var query = db.Orders.AsNoTracking()
            .Where(order => order.storefront.registration.vendor_id == vendorId
                && order.order_status != OrderStatuses.PendingPayment);
        if (status is not null)
        {
            query = query.Where(order => order.order_status == status);
        }

        return await BuildOrdersAsync(query, cancellationToken);
    }

    public async Task<CommerceOrderRow?> GetSellerOrderAsync(
        long vendorId,
        long orderId,
        CancellationToken cancellationToken) =>
        (await BuildOrdersAsync(
            db.Orders.AsNoTracking().Where(order => order.order_id == orderId
                && order.storefront.registration.vendor_id == vendorId
                && order.order_status != OrderStatuses.PendingPayment),
            cancellationToken)).SingleOrDefault();

    public async Task<OrderMutationResult> DecideSellerOrderAsync(
        long vendorId,
        long actorUserId,
        long orderId,
        string decision,
        string? reason,
        string expectedStatus,
        CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        var result = await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken);
            var order = await OwnedSellerOrderAsync(vendorId, orderId, cancellationToken);
            if (order is null)
            {
                return OrderMutationOutcome.NotFound;
            }

            if (order.order_status != expectedStatus || order.order_status != OrderStatuses.Placed)
            {
                return OrderMutationOutcome.Conflict;
            }

            var now = Now;
            if (decision == SellerOrderDecisions.Reject)
            {
                if (!await AddFullRefundAsync(order, RefundOrderRejected, cancellationToken))
                {
                    return OrderMutationOutcome.PaymentNotFound;
                }

                order.rejection_reason = reason;
                order.completed_at = now;
                Transition(order, OrderStatuses.Rejected, actorUserId, reason, now);
            }
            else
            {
                Transition(order, OrderStatuses.Accepted, actorUserId, null, now);
            }

            Notify(order.customer_user_id, "ORDER", "Trạng thái đơn hàng",
                decision == SellerOrderDecisions.Reject
                    ? $"Đơn {order.order_code} bị từ chối; yêu cầu hoàn tiền đang được xử lý."
                    : $"Đơn {order.order_code} đã được người bán nhận.",
                "Order", orderId);
            Audit(actorUserId, $"SORD_{decision}", "Order", orderId,
                new { expectedStatus, reason });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return OrderMutationOutcome.Updated;
        });

        return result == OrderMutationOutcome.Updated
            ? new OrderMutationResult(result, await GetSellerOrderAsync(
                vendorId, orderId, cancellationToken))
            : new OrderMutationResult(result, null);
    }

    public async Task<OrderMutationResult> UpdateSellerOrderStatusAsync(
        long vendorId,
        long actorUserId,
        long orderId,
        string targetStatus,
        string expectedStatus,
        CancellationToken cancellationToken)
    {
        var validTransition = expectedStatus == OrderStatuses.Accepted
            && targetStatus == OrderStatuses.Preparing
            || expectedStatus == OrderStatuses.Preparing
            && targetStatus == OrderStatuses.ReadyForPickup;
        if (!validTransition)
        {
            return new OrderMutationResult(OrderMutationOutcome.Conflict, null);
        }

        var outcome = await TransitionSellerOrderAsync(
            vendorId,
            actorUserId,
            orderId,
            expectedStatus,
            targetStatus,
            targetStatus == OrderStatuses.ReadyForPickup
                ? "Đơn hàng đã sẵn sàng để nhận."
                : "Người bán đang chuẩn bị đơn hàng.",
            cancellationToken);
        return outcome == OrderMutationOutcome.Updated
            ? new OrderMutationResult(outcome, await GetSellerOrderAsync(
                vendorId, orderId, cancellationToken))
            : new OrderMutationResult(outcome, null);
    }

    public Task<OrderMutationResult> ConfirmSellerHandoverAsync(
        long vendorId,
        long actorUserId,
        long orderId,
        string expectedStatus,
        CancellationToken cancellationToken) =>
        CompleteOrderAsync(actorUserId, vendorId, orderId, expectedStatus, true, cancellationToken);

    public async Task<CommerceSalesSummaryRow> GetSalesSummaryAsync(
        long vendorId,
        string period,
        CancellationToken cancellationToken)
    {
        var window = SalesPeriodWindowCalculator.Calculate(
            period,
            clock.GetUtcNow(),
            BusinessTimeZone);
        var orders = await BuildOrdersAsync(
            db.Orders.AsNoTracking().Where(order =>
                order.storefront.registration.vendor_id == vendorId
                && order.order_status == OrderStatuses.Completed
                && order.completed_at >= window.FromUtc
                && order.completed_at <= window.ToUtc),
            cancellationToken);
        var grossSales = orders.Sum(order => order.TotalAmount);
        var completedOrderIds = orders.Select(order => order.OrderId).ToArray();
        var refundedAmount = completedOrderIds.Length == 0
            ? 0
            : await db.RefundTransactions.AsNoTracking()
                .Where(refund => completedOrderIds.Contains(refund.order_id)
                    && refund.refund_status == RefundSuccess)
                .SumAsync(refund => (decimal?)refund.amount, cancellationToken) ?? 0;
        return new CommerceSalesSummaryRow(
            period,
            window.FromUtc,
            window.ToUtc,
            orders.Count,
            grossSales,
            refundedAmount,
            grossSales - refundedAmount,
            orders);
    }

    private IQueryable<MenuItem> MarketplaceMenuQuery() =>
        db.MenuItems.AsNoTracking()
            .Where(item => EligibleStores().Select(store => store.storefront_id).Contains(item.storefront_id)
                && (item.availability_status == MenuAvailable
                    || item.availability_status == MenuSoldOut));

    private static IQueryable<MarketplaceMenuItemRow> ProjectMarketplaceMenuItems(
        IQueryable<MenuItem> items) =>
        items.Select(item => new MarketplaceMenuItemRow(
                item.menu_item_id,
                item.storefront_id,
                item.storefront.storefront_name,
                item.item_name,
                item.description,
                item.image_url,
                item.unit_price,
                item.availability_status,
                item.category_id,
                item.category.category_name));

    private async Task<CommerceCartRow?> BuildCartAsync(
        long cartId,
        CancellationToken cancellationToken)
    {
        var header = await db.ShoppingCarts.AsNoTracking()
            .Where(cart => cart.cart_id == cartId && cart.cart_status == CartStatuses.Active)
            .Select(cart => new
            {
                cart.cart_id,
                cart.storefront_id,
                cart.storefront.storefront_name,
                StorefrontStatus = cart.storefront.availability_status,
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (header is null)
        {
            return null;
        }

        var items = await db.ShoppingCartItems.AsNoTracking()
            .Where(item => item.cart_id == cartId)
            .OrderBy(item => item.cart_item_id)
            .Select(item => new CommerceCartItemRow(
                item.cart_item_id,
                item.menu_item_id,
                item.menu_item.item_name,
                item.menu_item.image_url,
                item.menu_item.unit_price,
                item.menu_item.availability_status,
                item.quantity,
                item.note))
            .ToListAsync(cancellationToken);
        return new CommerceCartRow(
            header.cart_id,
            header.storefront_id,
            header.storefront_name,
            await EligibleStores().AnyAsync(x => x.storefront_id == header.storefront_id, cancellationToken) ? header.StorefrontStatus : "CLOSED",
            items,
            items.Sum(item => item.UnitPrice * item.Quantity));
    }

    private async Task<IReadOnlyList<CommerceOrderRow>> BuildOrdersAsync(
        IQueryable<Order> query,
        CancellationToken cancellationToken)
    {
        var headers = await query
            .OrderByDescending(order => order.created_at)
            .ThenByDescending(order => order.order_id)
            .Select(order => new OrderHeader(
                order.order_id,
                order.order_code,
                order.customer_user_id,
                order.customer_user.full_name ?? $"User #{order.customer_user_id}",
                order.storefront_id,
                order.storefront.storefront_name,
                order.order_status,
                order.subtotal_amount,
                order.total_amount,
                order.rejection_reason,
                order.placed_at,
                order.completed_at,
                order.created_at))
            .ToListAsync(cancellationToken);
        if (headers.Count == 0)
        {
            return [];
        }

        var ids = headers.Select(header => header.OrderId).ToArray();
        var items = await db.OrderItems.AsNoTracking()
            .Where(item => ids.Contains(item.order_id))
            .OrderBy(item => item.order_item_id)
            .Select(item => new
            {
                item.order_id,
                Row = new CommerceOrderItemRow(
                    item.order_item_id,
                    item.menu_item_id,
                    item.item_name_snapshot,
                    item.unit_price_snapshot,
                    item.quantity,
                    item.note),
            })
            .ToListAsync(cancellationToken);
        var histories = await db.OrderStatusHistories.AsNoTracking()
            .Where(item => ids.Contains(item.order_id))
            .OrderBy(item => item.changed_at)
            .ThenBy(item => item.history_id)
            .Select(item => new
            {
                item.order_id,
                Row = new CommerceOrderHistoryRow(
                    item.history_id,
                    item.from_status,
                    item.to_status,
                    item.note,
                    item.changed_at),
            })
            .ToListAsync(cancellationToken);
        var payments = await db.PaymentTransactions.AsNoTracking()
            .Where(payment => payment.order_id.HasValue
                && ids.Contains(payment.order_id.Value)
                && payment.payment_purpose == PaymentPurposeOrder)
            .OrderByDescending(payment => payment.created_at)
            .ThenByDescending(payment => payment.transaction_id)
            .Select(payment => new PaymentData(
                payment.order_id!.Value,
                payment.provider,
                payment.transaction_status))
            .ToListAsync(cancellationToken);
        var refunds = await db.RefundTransactions.AsNoTracking()
            .Where(refund => ids.Contains(refund.order_id))
            .OrderByDescending(refund => refund.requested_at)
            .ThenByDescending(refund => refund.refund_id)
            .Select(refund => new RefundData(
                refund.order_id,
                refund.amount,
                refund.refund_reason,
                refund.refund_status,
                refund.requested_at,
                refund.completed_at))
            .ToListAsync(cancellationToken);
        var itemsByOrder = items.GroupBy(item => item.order_id)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<CommerceOrderItemRow>)group
                .Select(item => item.Row).ToArray());
        var historyByOrder = histories.GroupBy(item => item.order_id)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<CommerceOrderHistoryRow>)group
                .Select(item => item.Row).ToArray());
        var paymentsByOrder = payments.GroupBy(payment => payment.OrderId)
            .ToDictionary(group => group.Key, group => group.First());
        var refundsByOrder = refunds.GroupBy(refund => refund.OrderId)
            .ToDictionary(group => group.Key, group => group.First());

        return headers.Select(header =>
        {
            paymentsByOrder.TryGetValue(header.OrderId, out var payment);
            refundsByOrder.TryGetValue(header.OrderId, out var refund);
            return new CommerceOrderRow(
                header.OrderId,
                header.OrderCode,
                header.CustomerUserId,
                header.CustomerName,
                header.StorefrontId,
                header.StorefrontName,
                header.OrderStatus,
                header.SubtotalAmount,
                header.TotalAmount,
                header.RejectionReason,
                payment?.Provider,
                payment?.Status,
                refund?.Amount,
                refund?.Reason,
                refund?.Status,
                refund?.RequestedAt,
                refund?.CompletedAt,
                header.PlacedAt,
                header.CompletedAt,
                header.CreatedAt,
                itemsByOrder.GetValueOrDefault(header.OrderId) ?? [],
                historyByOrder.GetValueOrDefault(header.OrderId) ?? []);
        }).ToArray();
    }

    private async Task<OrderMutationResult> BuildOrderResultAsync(
        (OrderMutationOutcome Outcome, long? OrderId) result,
        CancellationToken cancellationToken)
    {
        var row = result.Outcome == OrderMutationOutcome.Updated && result.OrderId.HasValue
            ? (await BuildOrdersAsync(
                db.Orders.AsNoTracking().Where(order => order.order_id == result.OrderId.Value),
                cancellationToken)).SingleOrDefault()
            : null;
        return new OrderMutationResult(result.Outcome, row);
    }

    private async Task<OrderMutationOutcome> TransitionSellerOrderAsync(
        long vendorId,
        long actorUserId,
        long orderId,
        string expectedStatus,
        string targetStatus,
        string notificationBody,
        CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken);
            var order = await OwnedSellerOrderAsync(vendorId, orderId, cancellationToken);
            if (order is null)
            {
                return OrderMutationOutcome.NotFound;
            }

            if (order.order_status != expectedStatus)
            {
                return OrderMutationOutcome.Conflict;
            }

            Transition(order, targetStatus, actorUserId, null, Now);
            Notify(order.customer_user_id, "ORDER", "Trạng thái đơn hàng",
                notificationBody, "Order", orderId);
            Audit(actorUserId, "SORD_STATUS_UPDATE", "Order", orderId,
                new { expectedStatus, targetStatus });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return OrderMutationOutcome.Updated;
        });
    }

    private async Task<OrderMutationResult> CompleteOrderAsync(
        long actorUserId,
        long? vendorId,
        long orderId,
        string expectedStatus,
        bool sellerAction,
        CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        var outcome = await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken);
            var order = sellerAction
                ? await OwnedSellerOrderAsync(vendorId!.Value, orderId, cancellationToken)
                : await db.Orders.SingleOrDefaultAsync(row => row.order_id == orderId
                    && row.customer_user_id == actorUserId, cancellationToken);
            if (order is null)
            {
                return OrderMutationOutcome.NotFound;
            }

            if (order.order_status != expectedStatus
                || order.order_status != OrderStatuses.ReadyForPickup)
            {
                return OrderMutationOutcome.Conflict;
            }

            var now = Now;
            Transition(order, OrderStatuses.Completed, actorUserId,
                sellerAction ? "Handover confirmed by seller." : "Pickup confirmed by customer.", now);
            order.completed_at = now;
            if (sellerAction)
            {
                Notify(order.customer_user_id, "ORDER", "Đơn hàng hoàn tất",
                    $"Người bán đã xác nhận bàn giao đơn {order.order_code}.", "Order", orderId);
            }
            else
            {
                var vendorUserId = await StorefrontVendorUserIdAsync(
                    order.storefront_id, cancellationToken);
                Notify(vendorUserId, "ORDER", "Đơn hàng hoàn tất",
                    $"Khách hàng đã xác nhận nhận đơn {order.order_code}.", "Order", orderId);
            }

            Audit(actorUserId, sellerAction ? "SORD_HANDOVER" : "ORD_PICKUP", "Order", orderId,
                new { expectedStatus });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return OrderMutationOutcome.Updated;
        });

        var orderRow = outcome == OrderMutationOutcome.Updated
            ? sellerAction
                ? await GetSellerOrderAsync(vendorId!.Value, orderId, cancellationToken)
                : await GetCustomerOrderAsync(actorUserId, orderId, cancellationToken)
            : null;
        return new OrderMutationResult(outcome, orderRow);
    }

    private Task<Order?> OwnedSellerOrderAsync(
        long vendorId,
        long orderId,
        CancellationToken cancellationToken) =>
        db.Orders.SingleOrDefaultAsync(order => order.order_id == orderId
            && order.storefront.registration.vendor_id == vendorId, cancellationToken);

    private async Task<bool> AddFullRefundAsync(
        Order order,
        string reason,
        CancellationToken cancellationToken)
    {
        var payment = await db.PaymentTransactions
            .Where(row => row.order_id == order.order_id
                && row.payment_purpose == PaymentPurposeOrder
                && row.transaction_status == PaymentSuccess)
            .OrderByDescending(row => row.created_at)
            .ThenByDescending(row => row.transaction_id)
            .FirstOrDefaultAsync(cancellationToken);
        if (payment is null)
        {
            return false;
        }

        var idempotencyKey = $"{reason}-{order.order_id}";
        if (!await db.RefundTransactions.AnyAsync(
                refund => refund.idempotency_key == idempotencyKey, cancellationToken))
        {
            db.RefundTransactions.Add(new RefundTransaction
            {
                order_id = order.order_id,
                payment_transaction_id = payment.transaction_id,
                idempotency_key = idempotencyKey,
                amount = payment.amount,
                refund_reason = reason,
                provider = payment.provider,
                refund_status = RefundPending,
                requested_at = Now,
            });
        }

        return true;
    }

    private static void Transition(
        Order order,
        string targetStatus,
        long? actorUserId,
        string? note,
        DateTime changedAt)
    {
        var previous = order.order_status;
        order.order_status = targetStatus;
        order.OrderStatusHistories.Add(new OrderStatusHistory
        {
            from_status = previous,
            to_status = targetStatus,
            changed_by = actorUserId,
            note = note,
            changed_at = changedAt,
        });
    }

    private Task<long> StorefrontVendorUserIdAsync(
        long storefrontId,
        CancellationToken cancellationToken) =>
        db.Storefronts.Where(row => row.storefront_id == storefrontId)
            .Select(row => row.registration.vendor.user_id)
            .SingleAsync(cancellationToken);

    private void Notify(
        long userId,
        string type,
        string title,
        string body,
        string entityType,
        long entityId) =>
        db.Notifications.Add(new Notification
        {
            user_id = userId,
            notification_type = type,
            title = title,
            body = body,
            related_entity_type = entityType,
            related_entity_id = entityId,
            is_read = false,
            sent_at = Now,
        });

    private void Audit(long actorUserId, string action, string entityType, long entityId, object details) =>
        db.AuditLogs.Add(new AuditLog
        {
            actor_user_id = actorUserId,
            action = action,
            entity_type = entityType,
            entity_id = entityId,
            details = JsonSerializer.Serialize(details),
            created_at = Now,
        });

    private sealed record OrderHeader(
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
        DateTime? PlacedAt,
        DateTime? CompletedAt,
        DateTime CreatedAt);

    private sealed record PaymentData(long OrderId, string Provider, string Status);

    private sealed record RefundData(
        long OrderId,
        decimal Amount,
        string Reason,
        string Status,
        DateTime RequestedAt,
        DateTime? CompletedAt);
}
