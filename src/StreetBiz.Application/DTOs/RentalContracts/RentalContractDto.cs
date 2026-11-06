using StreetBiz.Application.Common.Models;

namespace StreetBiz.Application.DTOs.RentalContracts;

public sealed record RentalContractDto(
    long ContractId,
    long ApplicationId,
    long SlotId,
    string SlotCode,
    string ZoneName,
    DateOnly StartDate,
    DateOnly EndDate,
    string ContractStatus,
    string? CancellationReason,
    DateTime? CancelledAt,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    /// <summary>
    /// Whether the contract has a digital permit yet, so a client only asks for permits that exist
    /// instead of probing every contract and logging a 404 for the ones without one.
    /// </summary>
    bool HasPermit = false);

/// <summary>Maps domain records to DTOs for the Rental Contract API.</summary>
public static class RentalContractMapper
{
    public static RentalContractDto ToDto(this RentalContractRow r) => new(
        r.ContractId, r.ApplicationId, r.SlotId, r.SlotCode, r.ZoneName,
        r.StartDate, r.EndDate, r.ContractStatus, r.CancellationReason, r.CancelledAt,
        r.CreatedAt, r.UpdatedAt, r.HasPermit);
}
