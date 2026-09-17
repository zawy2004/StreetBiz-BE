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
}
