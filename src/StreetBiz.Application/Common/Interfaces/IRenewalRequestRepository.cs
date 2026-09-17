using StreetBiz.Application.Common.Models;

namespace StreetBiz.Application.Common.Interfaces;

public interface IRenewalRequestRepository
{
    /// <summary>True when the contract already has a PENDING/UNDER_REVIEW renewal request.</summary>
    Task<bool> HasOpenAsync(long contractId, CancellationToken cancellationToken);

    Task<long> CreateAsync(long contractId, int requestedTermDays, CancellationToken cancellationToken);

    Task<RenewalRequestRow?> GetByIdAsync(long renewalId, CancellationToken cancellationToken);

    Task<IReadOnlyList<RenewalRequestRow>> ListByContractAsync(long contractId, CancellationToken cancellationToken);
}
