using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.API.Hubs;

[Authorize]
public sealed class OrderHub(
    ICurrentUser currentUser,
    IVendorContext vendorContext,
    ICommerceRepository orders) : Hub
{
    public const string OrderUpdatedEvent = "OrderUpdated";

    public static string GroupName(long orderId) => $"order:{orderId}";

    public async Task SubscribeOrder(long orderId)
    {
        if (orderId <= 0 || !await CanAccessOrderAsync(orderId))
        {
            // Do not reveal whether an order exists when it is owned by another account.
            throw new HubException("Order was not found or is not available to this account.");
        }

        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            GroupName(orderId),
            Context.ConnectionAborted);
    }

    public Task UnsubscribeOrder(long orderId) => Groups.RemoveFromGroupAsync(
        Context.ConnectionId,
        GroupName(orderId),
        Context.ConnectionAborted);

    private async Task<bool> CanAccessOrderAsync(long orderId)
    {
        var cancellationToken = Context.ConnectionAborted;
        if (currentUser.RoleCode == RoleCodes.Customer && currentUser.UserId is { } customerId)
        {
            return await orders.GetCustomerOrderAsync(
                customerId, orderId, cancellationToken) is not null;
        }

        if (currentUser.RoleCode == RoleCodes.Vendor)
        {
            var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);
            return await orders.GetSellerOrderAsync(
                vendorId, orderId, cancellationToken) is not null;
        }

        return false;
    }
}

public sealed record OrderUpdatedMessage(
    long OrderId,
    string OrderStatus,
    DateTime ChangedAtUtc);
