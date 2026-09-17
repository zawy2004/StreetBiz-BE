using StreetBiz.Application.Common.Models;

namespace StreetBiz.Application.DTOs.DigitalPermits;

public sealed record DigitalPermitDto(
    long PermitId,
    long ContractId,
    string QrPayload,
    DateOnly StartDate,
    DateOnly EndDate,
    string PermitStatus,
    string ContractStatus,
    string EffectiveStatus);

/// <summary>Maps domain records to DTOs for the Digital Permit API.</summary>
public static class DigitalPermitMapper
{
    public static DigitalPermitDto ToDto(this PermitValidityRow r) => new(
        r.PermitId, r.ContractId, r.QrPayload, r.StartDate, r.EndDate,
        r.PermitStatus, r.ContractStatus, r.EffectiveStatus);
}
