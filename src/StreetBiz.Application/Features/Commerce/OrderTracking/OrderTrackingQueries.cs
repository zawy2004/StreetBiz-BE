using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Commerce;

namespace StreetBiz.Application.Features.Commerce.OrderTracking;

/// <summary>ORD-01: a stall's pickup point and the range rule, for the checkout screen's live check.</summary>
public sealed record GetStorefrontPickupRangeQuery(long StorefrontId) : IRequest<PickupRangeInfoDto>;

public sealed class GetStorefrontPickupRangeQueryHandler(
    IOrderTrackingRepository tracking,
    IPickupRangePolicy policy) : IRequestHandler<GetStorefrontPickupRangeQuery, PickupRangeInfoDto>
{
    public async Task<PickupRangeInfoDto> Handle(
        GetStorefrontPickupRangeQuery request,
        CancellationToken cancellationToken)
    {
        // Slot coordinates are already public through discovery; nothing here is private.
        var point = await tracking.GetStorefrontPickupPointAsync(request.StorefrontId, cancellationToken)
            ?? throw new NotFoundException(CommerceMessages.StorefrontNotFound);
        var limits = policy.Limits;
        return new PickupRangeInfoDto(
            point.ToDto(),
            policy.Enforced,
            limits.RadiusMeters,
            limits.AccuracyAllowanceMeters,
            limits.MaxAccuracyMeters);
    }
}

/// <summary>ORD-02: when the order should be ready, how many are ahead of it, and where to collect it.</summary>
public sealed record GetOrderTrackingQuery(long OrderId) : IRequest<OrderTrackingDto>;

public sealed class GetOrderTrackingQueryHandler(
    ICustomerContext customerContext,
    IOrderTrackingRepository tracking,
    IDateTimeProvider clock) : IRequestHandler<GetOrderTrackingQuery, OrderTrackingDto>
{
    public async Task<OrderTrackingDto> Handle(GetOrderTrackingQuery request, CancellationToken cancellationToken)
    {
        var customerUserId = await customerContext.RequireCustomerUserIdAsync(cancellationToken);
        var order = await tracking.GetOrderTrackingAsync(customerUserId, request.OrderId, cancellationToken)
            ?? throw new NotFoundException(CommerceMessages.OrderNotFound);

        var arrival = order.ArrivalNotifiedAt is { } notified
            ? DateTime.SpecifyKind(notified, DateTimeKind.Utc)
            : (DateTime?)null;
        if (!IsBeingPrepared(order.OrderStatus))
        {
            return new OrderTrackingDto(
                order.OrderId, order.OrderStatus, order.PickupPoint.ToDto(), null, 0, order.ReadyAt)
            {
                ArrivalNotifiedAt = arrival,
            };
        }

        var now = clock.UtcNow;
        var samples = await tracking.GetRecentPrepMinutesAsync(
            order.PickupPoint.StorefrontId,
            now.AddDays(-PrepTimeEstimator.LookbackDays),
            PrepTimeEstimator.MaxSamples,
            cancellationToken);
        var estimate = PrepTimeEstimator.Estimate(samples);

        // Counted from acceptance: before that, the stall has not started and no clock time is honest.
        var earliest = order.AcceptedAt?.AddMinutes(estimate.LowMinutes);
        var latest = order.AcceptedAt?.AddMinutes(estimate.HighMinutes);
        var ready = new ReadyEstimateDto(
            estimate.LowMinutes,
            estimate.TypicalMinutes,
            estimate.HighMinutes,
            estimate.Basis,
            estimate.SampleSize,
            earliest,
            latest,
            latest.HasValue && now > latest.Value);

        return new OrderTrackingDto(
            order.OrderId, order.OrderStatus, order.PickupPoint.ToDto(), ready, order.OrdersAhead, order.ReadyAt)
        {
            ArrivalNotifiedAt = arrival,
        };
    }

    /// <summary>Paid and in the stall's hands, not yet ready: the only stretch an estimate is about.</summary>
    public static bool IsBeingPrepared(string status) =>
        status is OrderStatuses.Placed or OrderStatuses.Accepted or OrderStatuses.Preparing;
}

internal static class PickupPointMapping
{
    public static PickupPointDto ToDto(this PickupPointRow row) => new(
        row.StorefrontId, row.StorefrontName, row.Address, row.Latitude, row.Longitude);
}
