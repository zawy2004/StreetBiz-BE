using StreetBiz.Application.Common.Models;

namespace StreetBiz.Application.DTOs.DigitalPermits;

/// <param name="QrUrl"><c>{Permit:PublicScanBaseUrl}/q/{QrPayload}</c>: what the QR should encode so an
/// ordinary camera opens the public check page. Null when the base URL is not configured; the client
/// then draws <paramref name="QrPayload"/> itself, as before.</param>
public sealed record DigitalPermitDto(
    long PermitId,
    long ContractId,
    string QrPayload,
    DateOnly StartDate,
    DateOnly EndDate,
    string PermitStatus,
    string ContractStatus,
    string EffectiveStatus,
    string? QrUrl = null);

/// <summary>Maps domain records to DTOs for the Digital Permit API.</summary>
public static class DigitalPermitMapper
{
    public static DigitalPermitDto ToDto(this PermitValidityRow r) => new(
        r.PermitId, r.ContractId, r.QrPayload, r.StartDate, r.EndDate,
        r.PermitStatus, r.ContractStatus, r.EffectiveStatus);
}
