using StreetBiz.Application.Common.Models;

namespace StreetBiz.Application.Common.Interfaces;

public interface IRentalApplicationRepository
{
    /// <summary>True when the slot already has a PENDING/UNDER_REVIEW/MORE_INFORMATION_REQUIRED application.</summary>
    Task<bool> HasOpenApplicationForSlotAsync(long slotId, CancellationToken cancellationToken);

    Task<long> CreateAsync(
        long registrationId, long slotId, string applicationMethod, int requestedTermDays,
        CancellationToken cancellationToken);

    Task<RentalApplicationRow?> GetByIdAsync(long applicationId, CancellationToken cancellationToken);
}
