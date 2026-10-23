using StreetBiz.Application.Common.Models;

namespace StreetBiz.Application.DTOs.AddressChanges;

public sealed record AddressChangeRequestDto(
    long AddressChangeId,
    long RegistrationId,
    string NewAddress,
    decimal? NewLatitude,
    decimal? NewLongitude,
    long? ReleasedContractId,
    long? RequestedNewSlotId,
    string ChangeStatus,
    string? ConflictResolutionNote,
    DateTime? ReviewedAt,
    DateTime CreatedAt,
    string? ReleasedSlotCode = null,
    string? ReleaseState = null,
    DateOnly? ReleaseGraceEndsOn = null,
    bool ReleaseBlockedByDebt = false);

/// <summary>Maps domain records to DTOs for the Address Change API.</summary>
public static class AddressChangeRequestMapper
{
    public static AddressChangeRequestDto ToDto(this AddressChangeRequestRow r) => new(
        r.AddressChangeId, r.RegistrationId, r.NewAddress, r.NewLatitude, r.NewLongitude,
        r.ReleasedContractId, r.RequestedNewSlotId, r.ChangeStatus,
        r.ConflictResolutionNote, r.ReviewedAt, r.CreatedAt,
        r.ReleasedSlotCode, r.ReleaseState, r.ReleaseGraceEndsOn, r.ReleaseBlockedByDebt);
}
