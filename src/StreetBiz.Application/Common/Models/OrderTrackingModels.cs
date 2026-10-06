namespace StreetBiz.Application.Common.Models;

/// <summary>Where a pickup order is collected: the storefront's rented sidewalk slot.</summary>
public sealed record PickupPointRow(
    long StorefrontId,
    string StorefrontName,
    string? Address,
    double Latitude,
    double Longitude);

/// <summary>What ORD-02 tracking needs about one customer-owned order, beyond its OrderDto.</summary>
public sealed record OrderTrackingRow(
    long OrderId,
    string OrderStatus,
    DateTime? PlacedAt,
    DateTime? AcceptedAt,
    DateTime? ReadyAt,
    PickupPointRow PickupPoint,
    int OrdersAhead)
{
    /// <summary>When the customer last told the stall they were on their way, if they did.</summary>
    public DateTime? ArrivalNotifiedAt { get; init; }
}

/// <summary>A customer-owned order an arrival notice can be sent for, and to whom.</summary>
public sealed record ArrivalTargetRow(
    long OrderId,
    string OrderCode,
    string OrderStatus,
    long VendorUserId,
    DateTime? LastNotifiedAt);

/// <summary>A customer on their way to collect an order, as the stall sees it.</summary>
public sealed record OrderArrivalRow(long OrderId, string OrderCode, DateTime NotifiedAt, string Message);
