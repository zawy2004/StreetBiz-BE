using StreetBiz.Application.Common.Models;

namespace StreetBiz.Application.DTOs.RentalApplications;

public sealed record RentalApplicationDto(
    long ApplicationId,
    long RegistrationId,
    long SlotId,
    string ApplicationMethod,
    int RequestedTermDays,
    string ApplicationStatus,
    string? ReviewDecisionReason,
    DateTime? ReviewedAt,
    DateTime CreatedAt);

/// <summary>Maps domain records to DTOs for the Rental Application API.</summary>
public static class RentalApplicationMapper
{
    public static RentalApplicationDto ToDto(this RentalApplicationRow r) => new(
        r.ApplicationId, r.RegistrationId, r.SlotId, r.ApplicationMethod, r.RequestedTermDays,
        r.ApplicationStatus, r.ReviewDecisionReason, r.ReviewedAt, r.CreatedAt);
}
