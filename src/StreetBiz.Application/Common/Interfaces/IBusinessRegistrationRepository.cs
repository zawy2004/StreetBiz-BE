using StreetBiz.Application.Common.Models;

namespace StreetBiz.Application.Common.Interfaces;

public interface IBusinessRegistrationRepository
{
    Task<bool> HasActivePendingAsync(long vendorId, CancellationToken cancellationToken);
    Task<long> CreateAsync(long vendorId, NewBizRegistration data, CancellationToken cancellationToken);

    Task<BizRegistration?> GetByIdAsync(long registrationId, CancellationToken cancellationToken);
    Task<IReadOnlyList<BizRegistration>> ListByVendorAsync(long vendorId, CancellationToken cancellationToken);

    /// <summary>Updates editable fields and resets the status to SUBMITTED for re-review.</summary>
    Task UpdateAndResubmitAsync(long registrationId, NewBizRegistration data, CancellationToken cancellationToken);

    Task SetStatusAsync(long registrationId, string status, CancellationToken cancellationToken);

    Task<long> AddEvidenceAsync(long registrationId, NewRegistrationEvidence evidence, CancellationToken cancellationToken);

    /// <summary>True when an ACTIVE rental contract exists for this registration (blocks withdrawal, BR-16).</summary>
    Task<bool> HasActiveContractAsync(long registrationId, CancellationToken cancellationToken);
}
