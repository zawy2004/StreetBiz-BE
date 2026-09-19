namespace StreetBiz.Application.Common.Models;

/// <summary>Application-facing projection of a rental contract, joined with its slot and zone.</summary>
public sealed record RentalContractRow(
    long ContractId,
    long ApplicationId,
    long SlotId,
    string SlotCode,
    string ZoneName,
    long VendorId,
    DateOnly StartDate,
    DateOnly EndDate,
    string ContractStatus,
    long? CancelledBy,
    string? CancellationReason,
    DateTime? CancelledAt,
    DateTime CreatedAt,
    DateTime? UpdatedAt);
