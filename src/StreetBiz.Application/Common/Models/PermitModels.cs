namespace StreetBiz.Application.Common.Models;

/// <summary>
/// Projection of vw_PermitValidity (SIDE-08, WARD-11, BUY-02). EffectiveStatus is the only
/// correct answer to "is this permit good right now" -- it accounts for both permit_status
/// and contract_status plus today's date; permit_status alone is not enough.
/// </summary>
public sealed record PermitValidityRow(
    long PermitId,
    long ContractId,
    string QrPayload,
    long SlotId,
    long VendorId,
    DateOnly StartDate,
    DateOnly EndDate,
    string PermitStatus,
    string ContractStatus,
    string EffectiveStatus);
