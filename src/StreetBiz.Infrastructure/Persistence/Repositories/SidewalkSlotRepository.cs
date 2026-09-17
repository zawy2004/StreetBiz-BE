using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.Infrastructure.Persistence.Repositories;

public sealed class SidewalkSlotRepository(StreetBizDbContext dbContext) : ISidewalkSlotRepository
{
    public async Task<IReadOnlyList<SlotRow>> SearchAsync(SlotSearchArea area, CancellationToken cancellationToken)
    {
        // SidewalkSlots has no ward column of its own — its ward is PricingZones.ward_unit_id,
        // reached through zone_id, so a ward filter is always a join.
        var query = dbContext.SidewalkSlots.AsNoTracking()
            .Where(s => s.latitude >= area.MinLatitude && s.latitude <= area.MaxLatitude
                     && s.longitude >= area.MinLongitude && s.longitude <= area.MaxLongitude
                     && s.slot_status == SlotStatuses.Available
                     && (s.source == SlotSources.WardDefined
                         || s.proposal_review_status == ProposalReviewStatuses.Approved));

        if (area.WardUnitId is { } wardUnitId)
        {
            query = query.Where(s => s.zone.ward_unit_id == wardUnitId);
        }

        return await query.Select(ToRowExpression).ToListAsync(cancellationToken);
    }

    public Task<SlotRow?> GetByIdAsync(long slotId, CancellationToken cancellationToken) =>
        dbContext.SidewalkSlots.AsNoTracking()
            .Where(s => s.slot_id == slotId)
            .Select(ToRowExpression)
            .FirstOrDefaultAsync(cancellationToken);

    private static readonly System.Linq.Expressions.Expression<Func<ScaffoldedModels.SidewalkSlot, SlotRow>> ToRowExpression =
        s => new SlotRow(
            s.slot_id, s.slot_code, s.zone_id, s.zone.zone_name, s.zone.ward_unit_id,
            s.latitude, s.longitude, s.width_meters, s.length_meters,
            s.slot_status, s.source, s.zone.price_per_day, s.zone.available_from, s.zone.available_to);
}
