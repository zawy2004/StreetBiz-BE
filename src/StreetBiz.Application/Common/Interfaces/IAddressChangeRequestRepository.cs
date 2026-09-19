using StreetBiz.Application.Common.Models;

namespace StreetBiz.Application.Common.Interfaces;

public interface IAddressChangeRequestRepository
{
    /// <summary>True when the registration already has a PENDING/UNDER_REVIEW address change.</summary>
    Task<bool> HasOpenAsync(long registrationId, CancellationToken cancellationToken);

    Task<long> CreateAsync(long registrationId, NewAddressChangeRequest data, CancellationToken cancellationToken);

    Task<AddressChangeRequestRow?> GetByIdAsync(long addressChangeId, CancellationToken cancellationToken);

    Task<IReadOnlyList<AddressChangeRequestRow>> ListByVendorAsync(long vendorId, CancellationToken cancellationToken);
}
