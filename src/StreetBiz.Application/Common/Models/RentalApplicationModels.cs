namespace StreetBiz.Application.Common.Models;

/// <summary>Application-facing projection of a rental application.</summary>
public sealed record RentalApplicationRow(
    long ApplicationId,
    long RegistrationId,
    long SlotId,
    string ApplicationMethod,
    int RequestedTermDays,
    string ApplicationStatus,
    string? ReviewDecisionReason,
    DateTime? ReviewedAt,
    DateTime CreatedAt,
    long VendorId);
