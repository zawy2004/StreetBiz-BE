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

    /// <summary>How many "on my way" notices the customer has sent for this order.</summary>
    public int ArrivalNoticeCount { get; init; }

    /// <summary>When the stall answered "Đã thấy" to the latest notice; null if it has not.</summary>
    public DateTime? ArrivalAcknowledgedAt { get; init; }
}

/// <summary>A customer-owned order an arrival notice can be sent for, and to whom.</summary>
public sealed record ArrivalTargetRow(
    long OrderId,
    string OrderCode,
    string OrderStatus,
    long VendorUserId,
    DateTime? LastNotifiedAt,
    int NoticeCount);

/// <summary>An order of the vendor's stalls the "Đã thấy" answer is for, and who to tell.</summary>
public sealed record ArrivalAckTargetRow(
    long OrderId,
    string OrderCode,
    string OrderStatus,
    long CustomerUserId,
    string StorefrontName,
    DateTime? LastNoticeAt,
    DateTime? LastAckAt);

/// <summary>A customer on their way to collect an order, as the stall sees it.</summary>
public sealed record OrderArrivalRow(
    long OrderId, string OrderCode, DateTime NotifiedAt, string Message, DateTime? AcknowledgedAt);
