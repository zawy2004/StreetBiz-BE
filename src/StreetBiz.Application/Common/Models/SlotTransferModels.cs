namespace StreetBiz.Application.Common.Models;

/// <summary>Application-facing projection of a slot ownership transfer request.</summary>
public sealed record SlotTransferRequestRow(
    long TransferId,
    long ContractId,
    long FromVendorId,
    long ToVendorId,
    string TransferStatus,
    DateTime InitiatedAt,
    DateTime? AcceptedAt,
    string? ReviewDecisionReason,
    DateTime? ReviewedAt);
