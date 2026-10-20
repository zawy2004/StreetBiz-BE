using StreetBiz.Application.Common.Models;

namespace StreetBiz.Application.Common.Interfaces;

/// <summary>Read-only views of a vendor's rental fees, contract by contract (FEE-01, FEE-03, FEE-05).</summary>
public interface IVendorFinanceRepository
{
    /// <summary>The vendor's contracts that have a current (not superseded) fee schedule.</summary>
    Task<IReadOnlyList<VendorContractRow>> ListContractsAsync(long vendorId, CancellationToken cancellationToken);

    /// <summary>
    /// Every instalment of the vendor's current schedules, or of one contract's, numbered within
    /// its own schedule (Ordinal/OfCount), ordered by due date.
    /// </summary>
    Task<IReadOnlyList<ScheduleItemRow>> ListScheduleItemsAsync(
        long vendorId, long? contractId, CancellationToken cancellationToken);

    /// <summary>The vendor's display name and the business name on their registration, for documents.</summary>
    Task<(string VendorName, string? BusinessName)> GetVendorNamesAsync(long vendorId, CancellationToken cancellationToken);
}
