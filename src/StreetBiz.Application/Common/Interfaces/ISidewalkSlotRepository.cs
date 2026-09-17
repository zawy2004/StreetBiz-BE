using StreetBiz.Application.Common.Models;

namespace StreetBiz.Application.Common.Interfaces;

public interface ISidewalkSlotRepository
{
    /// <summary>
    /// AVAILABLE ward-defined slots, plus vendor-proposed slots already APPROVED by a ward
    /// officer (SIDE-01). A pending or rejected proposal never appears here.
    /// </summary>
    Task<IReadOnlyList<SlotRow>> SearchAsync(SlotSearchArea area, CancellationToken cancellationToken);

    Task<SlotRow?> GetByIdAsync(long slotId, CancellationToken cancellationToken);

    Task<bool> ZoneExistsAsync(int zoneId, CancellationToken cancellationToken);

    /// <summary>
    /// SIDE-11: inserts a VENDOR_PROPOSED slot pending WARD-16 review. Throws a typed
    /// AppException (via SqlErrorTranslator) if <paramref name="slotCode"/> collides.
    /// </summary>
    Task<long> ProposeAsync(long registrationId, NewSlotProposal proposal, string slotCode, CancellationToken cancellationToken);

    Task<SlotProposalRow?> GetProposalByIdAsync(long slotId, CancellationToken cancellationToken);

    Task<IReadOnlyList<SlotProposalRow>> ListProposalsByVendorAsync(long vendorId, CancellationToken cancellationToken);
}
