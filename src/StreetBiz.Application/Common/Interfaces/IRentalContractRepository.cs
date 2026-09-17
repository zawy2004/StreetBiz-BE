using StreetBiz.Application.Common.Models;

namespace StreetBiz.Application.Common.Interfaces;

public interface IRentalContractRepository
{
    /// <summary>BR-12: true when the registration already holds an ACTIVE storefront-adjacent contract.</summary>
    Task<bool> HasActiveAdjacentContractAsync(long registrationId, CancellationToken cancellationToken);

    Task<IReadOnlyList<RentalContractRow>> ListByVendorAsync(long vendorId, string? status, CancellationToken cancellationToken);

    Task<RentalContractRow?> GetByIdAsync(long contractId, CancellationToken cancellationToken);

    /// <summary>True when the contract has an OVERDUE fee item or an UNPAID penalty (blocks SIDE-07 return).</summary>
    Task<bool> HasOutstandingDebtAsync(long contractId, CancellationToken cancellationToken);

    /// <summary>
    /// SIDE-07: marks the contract CANCELLED and frees its slot back to AVAILABLE. Throws a
    /// typed AppException (via SqlErrorTranslator) if TR_RentalContracts_NoCancelWithDebt fires
    /// despite the caller's own pre-check, e.g. under a race with a fee just turning OVERDUE.
    /// </summary>
    Task CancelAsync(long contractId, long cancelledByUserId, string? reason, CancellationToken cancellationToken);
}
