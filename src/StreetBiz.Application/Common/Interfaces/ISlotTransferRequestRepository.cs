using StreetBiz.Application.Common.Models;

namespace StreetBiz.Application.Common.Interfaces;

public interface ISlotTransferRequestRepository
{
    /// <summary>True when the contract already has a PENDING/ACCEPTED_BY_RECEIVER transfer request.</summary>
    Task<bool> HasOpenForContractAsync(long contractId, CancellationToken cancellationToken);

    Task<long> CreateAsync(long contractId, long fromVendorId, long toVendorId, CancellationToken cancellationToken);

    Task<SlotTransferRequestRow?> GetByIdAsync(long transferId, CancellationToken cancellationToken);

    /// <summary>direction is "outgoing" (caller is from_vendor) or "incoming" (caller is to_vendor).</summary>
    Task<IReadOnlyList<SlotTransferRequestRow>> ListByVendorAsync(long vendorId, string direction, CancellationToken cancellationToken);

    Task SetStatusAsync(long transferId, string status, DateTime? acceptedAt, CancellationToken cancellationToken);
}
