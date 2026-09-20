using StreetBiz.Application.Common.Models;

namespace StreetBiz.Application.DTOs.SlotTransfers;

public sealed record SlotTransferRequestDto(
    long TransferId,
    long ContractId,
    long FromVendorId,
    long ToVendorId,
    string TransferStatus,
    DateTime InitiatedAt,
    DateTime? AcceptedAt,
    string? ReviewDecisionReason,
    DateTime? ReviewedAt);

/// <summary>Maps domain records to DTOs for the Slot Transfer API.</summary>
public static class SlotTransferRequestMapper
{
    public static SlotTransferRequestDto ToDto(this SlotTransferRequestRow r) => new(
        r.TransferId, r.ContractId, r.FromVendorId, r.ToVendorId, r.TransferStatus,
        r.InitiatedAt, r.AcceptedAt, r.ReviewDecisionReason, r.ReviewedAt);
}
