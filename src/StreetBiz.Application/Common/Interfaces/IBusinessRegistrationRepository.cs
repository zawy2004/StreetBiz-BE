using StreetBiz.Application.Common.Models;

namespace StreetBiz.Application.Common.Interfaces;

public interface IBusinessRegistrationRepository
{
    /// <summary>True when the vendor has a SUBMITTED/UNDER_REVIEW registration other than <paramref name="excludeRegistrationId"/>.</summary>
    Task<bool> HasActivePendingAsync(long vendorId, CancellationToken cancellationToken, long? excludeRegistrationId = null);
    Task<long> CreateAsync(long vendorId, NewBizRegistration data, CancellationToken cancellationToken);

    Task<BizRegistration?> GetByIdAsync(long registrationId, CancellationToken cancellationToken);
    Task<IReadOnlyList<BizRegistration>> ListByVendorAsync(long vendorId, CancellationToken cancellationToken);

    /// <summary>Updates editable fields and resets the status to SUBMITTED for re-review.</summary>
    Task UpdateAndResubmitAsync(long registrationId, NewBizRegistration data, CancellationToken cancellationToken);

    Task SetStatusAsync(long registrationId, string status, CancellationToken cancellationToken);

    Task<long> AddEvidenceAsync(long registrationId, NewRegistrationEvidence evidence, CancellationToken cancellationToken);

    Task<IReadOnlyList<BizRegistrationEvidence>> ListEvidenceAsync(long registrationId, CancellationToken cancellationToken);

    /// <summary>True when an ACTIVE rental contract exists for this registration (blocks withdrawal, BR-16).</summary>
    Task<bool> HasActiveContractAsync(long registrationId, CancellationToken cancellationToken);

    /// <summary>BR-26 (SIDE-12/13): true when the vendor holds at least one APPROVED registration.</summary>
    Task<bool> HasApprovedRegistrationAsync(long vendorId, CancellationToken cancellationToken);

    /// <summary>
    /// Separate, explicit consent to run AI-OCR on the registration's uploaded ID photo
    /// (biometric data). Luat Bao ve du lieu ca nhan 2025 / Nghi dinh 356/2025/ND-CP requires
    /// this to be its own affirmative action, not folded into a general terms checkbox.
    /// </summary>
    Task RecordBiometricConsentAsync(long registrationId, CancellationToken cancellationToken);
}
