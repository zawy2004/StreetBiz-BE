using StreetBiz.Application.Common.Models;

namespace StreetBiz.Application.Common.Interfaces;

public interface IRenewalRequestRepository
{
    /// <summary>True when the contract already has a PENDING/UNDER_REVIEW renewal request.</summary>
    Task<bool> HasOpenAsync(long contractId, CancellationToken cancellationToken);

    Task<long> CreateAsync(long contractId, int requestedTermDays, CancellationToken cancellationToken);

    Task<RenewalRequestRow?> GetByIdAsync(long renewalId, CancellationToken cancellationToken);

    Task<IReadOnlyList<RenewalRequestRow>> ListByContractAsync(long contractId, CancellationToken cancellationToken);

    /// <summary>SIDE-06: vendor withdraws their own open renewal request. Returns false if it
    /// was no longer PENDING/UNDER_REVIEW (already decided by the ward, or already withdrawn).</summary>
    Task<bool> WithdrawAsync(long renewalId, CancellationToken cancellationToken);

    /// <summary>Cascades from SIDE-07 (voluntary contract cancellation): withdraws any renewal
    /// request still open on that contract, so it doesn't sit forever in the ward's queue for a
    /// contract that no longer exists to extend.</summary>
    Task CloseOpenForContractAsync(long contractId, CancellationToken cancellationToken);
}
