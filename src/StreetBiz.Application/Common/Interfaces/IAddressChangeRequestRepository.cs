using StreetBiz.Application.Common.Models;

namespace StreetBiz.Application.Common.Interfaces;

public interface IAddressChangeRequestRepository
{
    /// <summary>True when the registration already has a PENDING/UNDER_REVIEW address change.</summary>
    Task<bool> HasOpenAsync(long registrationId, CancellationToken cancellationToken);

    Task<long> CreateAsync(long registrationId, NewAddressChangeRequest data, CancellationToken cancellationToken);

    Task<AddressChangeRequestRow?> GetByIdAsync(long addressChangeId, CancellationToken cancellationToken);

    Task<IReadOnlyList<AddressChangeRequestRow>> ListByVendorAsync(long vendorId, CancellationToken cancellationToken);

    /// <summary>
    /// SIDE-10: every open address change whose named slot is still an ACTIVE contract, with how long
    /// the ward has extended its grace and whether anything is owed on it (an overdue fee or an unpaid penalty).
    /// </summary>
    Task<IReadOnlyList<PendingReleaseRow>> ListPendingReleasesAsync(CancellationToken cancellationToken);

    /// <summary>Tells the vendor the slot went back (MSG20) and leaves the audit row; the contract itself was cancelled by the caller.</summary>
    Task RecordReleaseAsync(PendingReleaseRow release, CancellationToken cancellationToken);

    /// <summary>
    /// Tells the vendor the grace period is over but the slot is held back by what they owe. At most
    /// one such notice per request per calendar day, so the hourly sweep does not repeat it.
    /// </summary>
    Task NotifyReleaseBlockedAsync(PendingReleaseRow release, DateOnly today, CancellationToken cancellationToken);
}
