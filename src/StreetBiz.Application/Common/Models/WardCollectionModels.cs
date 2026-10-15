namespace StreetBiz.Application.Common.Models;

/// <summary>An instalment of a current fee schedule in the ward, still unpaid, with who owes it.</summary>
public sealed record WardUnpaidItemRow(
    long FeeItemId,
    long ContractId,
    long VendorUserId,
    string VendorName,
    string? VendorPhone,
    string? BusinessName,
    int ZoneId,
    string ZoneName,
    string SlotCode,
    DateOnly DueDate,
    decimal Amount);

/// <summary>
/// An instalment in the ward that fell due or was paid inside a window. <c>IsCurrent</c> is false
/// for a superseded revision: money it collected is still revenue, but it is no longer "due".
/// </summary>
public sealed record WardFeeItemRow(
    int ZoneId,
    DateOnly DueDate,
    decimal Amount,
    string ItemStatus,
    DateTime? PaidAt,
    bool IsCurrent);

public sealed record WardZoneRow(int ZoneId, string ZoneName, int SlotCount, int RentedSlots);

public sealed record WardPenaltyPaymentRow(decimal Amount, DateTime PaidAt);

/// <summary>
/// A fee or penalty payment the ward received but could not apply (REFUND_REQUIRED), or has
/// since paid back (REFUNDED). <c>Kind</c> is FEE or PENALTY; <c>Label</c> says what was being
/// paid for. <c>RefundedAt</c> comes from the audit entry written when it was marked refunded.
/// </summary>
public sealed record WardRefundRow(
    long TransactionId,
    string Kind,
    long VendorUserId,
    string VendorName,
    string? VendorPhone,
    string? SlotCode,
    string Label,
    string Provider,
    string? ProviderReference,
    decimal Amount,
    DateTime ReceivedAt,
    string Status,
    DateTime? RefundedAt);
