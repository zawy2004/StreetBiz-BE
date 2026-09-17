namespace StreetBiz.Application.Common.Models;

/// <summary>Application-facing projection of a rental-contract renewal request.</summary>
public sealed record RenewalRequestRow(
    long RenewalId,
    long ContractId,
    int RequestedTermDays,
    string RenewalStatus,
    DateOnly? NewEndDate,
    string? ReviewDecisionReason,
    DateTime? ReviewedAt,
    DateTime CreatedAt);
