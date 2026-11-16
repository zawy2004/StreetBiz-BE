using StreetBiz.Application.Common.Models;

namespace StreetBiz.Application.Common.Interfaces;

public interface IRentalApplicationRepository
{
    /// <summary>True when the slot already has a PENDING/UNDER_REVIEW/MORE_INFORMATION_REQUIRED application.</summary>
    Task<bool> HasOpenApplicationForSlotAsync(long slotId, CancellationToken cancellationToken);

    /// <summary>
    /// Creates the application, or returns null when the slot already has an open one. The check
    /// and the insert are one atomic step per slot: two submissions racing for the same slot (a
    /// double tap, a retried request) produce one application and one refusal, never two rows.
    /// </summary>
    /// <param name="commitmentsAcceptedAt">When the vendor ticked the commitments; null for flows that have none.</param>
    Task<long?> CreateAsync(
        long registrationId, long slotId, string applicationMethod, int requestedTermDays,
        DateTime? commitmentsAcceptedAt, CancellationToken cancellationToken);

    Task<RentalApplicationRow?> GetByIdAsync(long applicationId, CancellationToken cancellationToken);

    Task<IReadOnlyList<RentalApplicationRow>> ListByVendorAsync(long vendorId, CancellationToken cancellationToken);

    Task SetStatusAsync(long applicationId, string status, CancellationToken cancellationToken);
}
