using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using StreetBiz.Application.Common.Exceptions;
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

    /// <summary>Any order of this seller changed: a new one was paid, one moved, one closed.</summary>
    public const string VendorOrderChangedEvent = "VendorOrderChanged";

    public static string GroupName(long orderId) => $"order:{orderId}";

    public static string VendorGroupName(long vendorId) => $"vendor:{vendorId}";

    /// <summary>
    /// A seller's whole board: every change to any of their orders, including
    /// orders that did not exist when they subscribed. The vendor is read from
    /// the caller's own token and never passed in, so nobody can listen to
    /// another stall's orders.
    /// </summary>
    public async Task SubscribeVendorOrders()
    {
        var vendorId = await CallerVendorIdAsync()
            ?? throw new HubException("Only a seller can follow their orders.");
        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            VendorGroupName(vendorId),
            Context.ConnectionAborted);
    }

    public async Task UnsubscribeVendorOrders()
    {
        if (await CallerVendorIdAsync() is { } vendorId)
        {
            await Groups.RemoveFromGroupAsync(
                Context.ConnectionId,
                VendorGroupName(vendorId),
                Context.ConnectionAborted);
        }
    }

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

    private async Task<long?> CallerVendorIdAsync()
    {
        if (currentUser.RoleCode != RoleCodes.Vendor)
        {
            return null;
        }

        try
        {
            return await vendorContext.RequireVendorIdAsync(Context.ConnectionAborted);
        }
        catch (AppException)
        {
            // A vendor account with no seller profile has no board to follow.
            return null;
        }
    }

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
