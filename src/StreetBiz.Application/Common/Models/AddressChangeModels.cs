namespace StreetBiz.Application.Common.Models;

/// <summary>Application-facing projection of an address change request.</summary>
public sealed record AddressChangeRequestRow(
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
    // SIDE-10: where the slot being given up stands. All null when the request names no slot to release.
    string? ReleasedSlotCode = null,
    string? ReleaseState = null,
    DateOnly? ReleaseGraceEndsOn = null,
    bool ReleaseBlockedByDebt = false);

/// <summary>Fields supplied by a vendor requesting an address change (SIDE-09/10).</summary>
public sealed record NewAddressChangeRequest(
    string NewAddress,
    decimal? NewLatitude,
    decimal? NewLongitude,
    long? ReleasedContractId,
    long? RequestedNewSlotId);

/// <summary>An open address change whose previous slot is still the vendor's and waiting to be released (SIDE-10).</summary>
public sealed record PendingReleaseRow(
    long AddressChangeId,
    long RegistrationId,
    long VendorId,
    long VendorUserId,
    long ContractId,
    string SlotCode,
    DateTime RequestedAt,
    int ExtensionDays,
    bool HasDebt)
{
    public DateOnly GraceEndsOn =>
        Security.AdjacentSlotRelease.GraceEndsOn(RequestedAt, ExtensionDays);
}

public sealed record AdjacentSlotReleaseSweepResult(int Released, int WaitingForPayment);
