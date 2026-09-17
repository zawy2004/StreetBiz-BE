using StreetBiz.Application.Common.Models;

namespace StreetBiz.Application.Common.Interfaces;

public interface IDigitalPermitRepository
{
    /// <summary>
    /// The live (or most recently revoked, if none is live) permit for a contract, read from
    /// vw_PermitValidity. Null when no permit has been issued for the contract yet.
    /// </summary>
    Task<PermitValidityRow?> GetValidityByContractAsync(long contractId, CancellationToken cancellationToken);
}
