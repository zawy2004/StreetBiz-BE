namespace StreetBiz.Application.DTOs.Commerce;

public sealed record PickupPointDto(
    long StorefrontId,
    string StorefrontName,
    string? Address,
    double Latitude,
    double Longitude);

/// <summary>
/// ORD-01: what the checkout screen needs to judge distance live while the customer moves. The
/// server still decides at checkout; the client only uses this to explain the decision early.
/// </summary>
public sealed record PickupRangeInfoDto(
    PickupPointDto PickupPoint,
    bool Enforced,
    int RadiusMeters,
    int AccuracyAllowanceMeters,
    int MaxAccuracyMeters);

/// <summary>
/// When an order should be ready. The minutes are the stall's usual preparation range; the two
/// instants are that range counted from when the stall accepted this order (null until it has).
/// </summary>
public sealed record ReadyEstimateDto(
    int LowMinutes,
    int TypicalMinutes,
    int HighMinutes,
    string Basis,
    int SampleSize,
    DateTime? EarliestReadyAt,
    DateTime? LatestReadyAt,
    bool IsLate);

/// <summary>ORD-02: the forward-looking half of tracking. The status history stays on OrderDto.</summary>
public sealed record OrderTrackingDto(
    long OrderId,
    string OrderStatus,
    PickupPointDto PickupPoint,
    ReadyEstimateDto? ReadyEstimate,
    int OrdersAhead,
    DateTime? ReadyAt)
{
    /// <summary>When the customer last told the stall "I'm on my way" for this order.</summary>
    public DateTime? ArrivalNotifiedAt { get; init; }
}

/// <summary>The stall has been told the customer is on the way.</summary>
public sealed record ArrivalNoticeDto(long OrderId, string OrderStatus, DateTime NotifiedAt, bool AlreadySent);

/// <summary>A customer on the way to collect, for the seller's order board.</summary>
public sealed record OrderArrivalDto(long OrderId, string OrderCode, DateTime NotifiedAt, string Message);
