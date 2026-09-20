namespace StreetBiz.Application.Common.Models;

/// <summary>
/// Application-facing projection of a slot ownership transfer request. Carries the slot and term of the
/// contract being handed over, because the receiver cannot read that contract (only its holder can) yet has
/// to know what they are accepting.
/// </summary>
public sealed record SlotTransferRequestRow(
    long TransferId,
    long ContractId,
    long FromVendorId,
    long ToVendorId,
    string TransferStatus,
    DateTime InitiatedAt,
    DateTime? AcceptedAt,
    string? ReviewDecisionReason,
    DateTime? ReviewedAt,
    string SlotCode,
    string ZoneName,
    DateOnly ContractStartDate,
    DateOnly ContractEndDate);
