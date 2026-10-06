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
