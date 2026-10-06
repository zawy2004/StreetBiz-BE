using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StreetBiz.Application.DTOs.Commerce;
using StreetBiz.Application.Features.Commerce.OrderTracking;
using StreetBiz.API.Hubs;

namespace StreetBiz.API.Controllers;

public sealed record AnnounceArrivalRequest(int? EtaMinutes);

/// <summary>
/// ORD-01 pickup range and ORD-02 tracking. Orders themselves are written by OrdersController;
/// this only reads them, and sends the customer's "on my way" notice to the stall.
/// </summary>
[ApiController]
public sealed class OrderTrackingController(ISender sender, IOrderRealtimePublisher realtime) : ControllerBase
{
    /// <summary>The customer tells the stall they are on the way (at most once every 2 minutes).</summary>
    [Authorize]
    [HttpPost("api/orders/{orderId:long}/arriving")]
    public async Task<ActionResult<ArrivalNoticeDto>> AnnounceArrival(
        long orderId,
        AnnounceArrivalRequest? request,
        CancellationToken cancellationToken)
    {
        var notice = await sender.Send(new AnnounceArrivalCommand(orderId, request?.EtaMinutes), cancellationToken);
        if (!notice.AlreadySent)
        {
            // The seller's open order screens refetch on this, as on any change to the order.
            await realtime.PublishAsync(orderId, notice.OrderStatus, cancellationToken);
        }

        return Ok(notice);
    }

    /// <summary>The seller's board: customers on their way to collect, most recent first.</summary>
    [Authorize]
    [HttpGet("api/vendor/orders/arrivals")]
    public async Task<ActionResult<IReadOnlyList<OrderArrivalDto>>> Arrivals(CancellationToken cancellationToken) =>
        Ok(await sender.Send(new ListVendorArrivalsQuery(), cancellationToken));

    /// <summary>
    /// Public, like the rest of the marketplace: the checkout screen measures the customer's live
    /// distance against this before the server makes the binding check at checkout.
    /// </summary>
    [HttpGet("api/marketplace/storefronts/{storefrontId:long}/pickup-range")]
    public async Task<ActionResult<PickupRangeInfoDto>> PickupRange(
        long storefrontId,
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(new GetStorefrontPickupRangeQuery(storefrontId), cancellationToken));

    /// <summary>The owning customer only; another customer's order is a 404, never a 403.</summary>
    [Authorize]
    [HttpGet("api/orders/{orderId:long}/tracking")]
    public async Task<ActionResult<OrderTrackingDto>> Tracking(
        long orderId,
        CancellationToken cancellationToken) =>
        Ok(await sender.Send(new GetOrderTrackingQuery(orderId), cancellationToken));
}
