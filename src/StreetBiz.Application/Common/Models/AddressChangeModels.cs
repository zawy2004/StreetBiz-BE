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
    DateTime CreatedAt);

/// <summary>Fields supplied by a vendor requesting an address change (SIDE-09/10).</summary>
public sealed record NewAddressChangeRequest(
    string NewAddress,
    decimal? NewLatitude,
    decimal? NewLongitude,
    long? ReleasedContractId,
    long? RequestedNewSlotId);
