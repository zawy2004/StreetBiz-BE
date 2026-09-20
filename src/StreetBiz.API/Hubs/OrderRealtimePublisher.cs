using Microsoft.AspNetCore.SignalR;
using StreetBiz.Application.DTOs.Commerce;

namespace StreetBiz.API.Hubs;

public interface IOrderRealtimePublisher
{
    Task PublishAsync(OrderDto order, CancellationToken cancellationToken = default);
    Task PublishAsync(long orderId, string orderStatus, CancellationToken cancellationToken = default);
}

public sealed class OrderRealtimePublisher(
    IHubContext<OrderHub> hub,
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
        try
        {
            await hub.Clients.Group(OrderHub.GroupName(orderId)).SendAsync(
                OrderHub.OrderUpdatedEvent,
                new OrderUpdatedMessage(orderId, orderStatus, DateTime.UtcNow),
                cancellationToken);
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
