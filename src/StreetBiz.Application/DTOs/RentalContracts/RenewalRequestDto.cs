using StreetBiz.Application.Common.Models;

namespace StreetBiz.Application.DTOs.RentalContracts;

public sealed record RenewalRequestDto(
    long RenewalId,
    long ContractId,
    int RequestedTermDays,
    string RenewalStatus,
    DateOnly? NewEndDate,
    string? ReviewDecisionReason,
    DateTime? ReviewedAt,
    DateTime CreatedAt);

/// <summary>Maps domain records to DTOs for the renewal-request API.</summary>
public static class RenewalRequestMapper
{
    public static RenewalRequestDto ToDto(this RenewalRequestRow r) => new(
        r.RenewalId, r.ContractId, r.RequestedTermDays, r.RenewalStatus, r.NewEndDate,
        r.ReviewDecisionReason, r.ReviewedAt, r.CreatedAt);
}
