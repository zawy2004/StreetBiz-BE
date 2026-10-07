using Microsoft.AspNetCore.SignalR;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Commerce;

namespace StreetBiz.API.Hubs;

public interface IOrderRealtimePublisher
{
    Task PublishAsync(OrderDto order, CancellationToken cancellationToken = default);
    Task PublishAsync(long orderId, string orderStatus, CancellationToken cancellationToken = default);
}

/// <summary>
/// Tells everyone watching an order that it changed - the buyer and seller on its
/// own screen, and the seller's order board, which has to hear about orders it
/// has never seen (a new one, paid a second ago). Clients refetch over HTTP: the
/// message says what changed, never the order itself.
/// </summary>
public sealed class OrderRealtimePublisher(
    IHubContext<OrderHub> hub,
    IServiceScopeFactory scopes,
    ILogger<OrderRealtimePublisher> logger) : IOrderRealtimePublisher
{
    public Task PublishAsync(
        OrderDto order,
        CancellationToken cancellationToken = default) =>
        PublishAsync(order.OrderId, order.OrderStatus, cancellationToken);

    public async Task PublishAsync(
        long orderId,
        string orderStatus,
        CancellationToken cancellationToken = default)
    {
        var message = new OrderUpdatedMessage(orderId, orderStatus, DateTime.UtcNow);
        try
        {
            await hub.Clients.Group(OrderHub.GroupName(orderId)).SendAsync(
                OrderHub.OrderUpdatedEvent, message, cancellationToken);

            // An unpaid order is not on any seller's board yet; the payment that
            // turns it into PLACED is what puts it there, and that is published too.
            if (orderStatus == OrderStatuses.PendingPayment)
            {
                return;
            }

            await using var scope = scopes.CreateAsyncScope();
            var vendorId = await scope.ServiceProvider
                .GetRequiredService<ICommerceRepository>()
                .GetOrderVendorIdAsync(orderId, cancellationToken);
            if (vendorId is { } id)
            {
                await hub.Clients.Group(OrderHub.VendorGroupName(id)).SendAsync(
                    OrderHub.VendorOrderChangedEvent, message, cancellationToken);
            }
        }
        catch (Exception exception)
        {
            // The order transaction is already committed. A realtime transport
            // failure must not turn a successful mutation into an HTTP 500;
            // disconnected clients will use their polling fallback.
            logger.LogWarning(
                exception,
                "Could not publish realtime update for order {OrderId}.",
                orderId);
        }
    }
}
