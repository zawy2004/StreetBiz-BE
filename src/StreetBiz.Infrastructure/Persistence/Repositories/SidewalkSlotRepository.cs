using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.Infrastructure.Persistence.Repositories;

public sealed class SidewalkSlotRepository(StreetBizDbContext dbContext) : ISidewalkSlotRepository
{
    public async Task<IReadOnlyList<SlotRow>> SearchAsync(SlotSearchArea area, CancellationToken cancellationToken)
    {
        var query = dbContext.SidewalkSlots.AsNoTracking()
            // A vendor-proposed slot is invisible until WARD-16 approves it. This rule is
            // unconditional: IncludeUnavailable widens slot_status only, never this.
            .Where(s => s.source == SlotSources.WardDefined
                     || s.proposal_review_status == ProposalReviewStatuses.Approved);

        if (!area.IncludeUnavailable)
        {
            query = query.Where(s => s.slot_status == SlotStatuses.Available);
        }

        if (area.MinLatitude is { } minLat) query = query.Where(s => s.latitude >= minLat);
        if (area.MaxLatitude is { } maxLat) query = query.Where(s => s.latitude <= maxLat);
        if (area.MinLongitude is { } minLng) query = query.Where(s => s.longitude >= minLng);
        if (area.MaxLongitude is { } maxLng) query = query.Where(s => s.longitude <= maxLng);

        if (area.ZoneId is { } zoneId)
        {
            query = query.Where(s => s.zone_id == zoneId);
        }

        // SidewalkSlots has no ward column of its own — its ward is PricingZones.ward_unit_id,
        // reached through zone_id, so a ward filter is always a join.
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

    public Task<bool> ZoneExistsAsync(int zoneId, CancellationToken cancellationToken) =>
        dbContext.PricingZones.AsNoTracking().AnyAsync(z => z.zone_id == zoneId, cancellationToken);

    public async Task<long> ProposeAsync(
        long registrationId, NewSlotProposal proposal, string slotCode, CancellationToken cancellationToken)
    {
        var entity = new ScaffoldedModels.SidewalkSlot
        {
            slot_code = slotCode,
            zone_id = proposal.ZoneId,
            latitude = proposal.Latitude,
            longitude = proposal.Longitude,
            width_meters = proposal.WidthMeters,
            length_meters = proposal.LengthMeters,
            source = SlotSources.VendorProposed,
            proposed_by_registration_id = registrationId,
            proposal_review_status = ProposalReviewStatuses.Pending,
            proposal_photo_url = proposal.ProposalPhotoUrl,
            // slot_status keeps its AVAILABLE default: WARD-16's proposal_review_status is what
            // gates visibility on SIDE-01 (already filtered to APPROVED proposals there).
        };

        dbContext.SidewalkSlots.Add(entity);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            var translated = SqlErrorTranslator.TryTranslate(ex);
            if (translated is not null)
            {
                throw translated;
            }

            throw;
        }

        return entity.slot_id;
    }

    public Task<SlotProposalRow?> GetProposalByIdAsync(long slotId, CancellationToken cancellationToken) =>
        dbContext.SidewalkSlots.AsNoTracking()
            .Where(s => s.slot_id == slotId)
            .Select(ToProposalRowExpression)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<SlotProposalRow>> ListProposalsByVendorAsync(long vendorId, CancellationToken cancellationToken) =>
        await dbContext.SidewalkSlots.AsNoTracking()
            .Where(s => s.proposed_by_registration != null && s.proposed_by_registration.vendor_id == vendorId)
            .OrderByDescending(s => s.created_at)
            .Select(ToProposalRowExpression)
            .ToListAsync(cancellationToken);

    private static readonly System.Linq.Expressions.Expression<Func<ScaffoldedModels.SidewalkSlot, SlotRow>> ToRowExpression =
        s => new SlotRow(
            s.slot_id, s.slot_code, s.zone_id, s.zone.zone_name, s.zone.ward_unit_id,
            s.latitude, s.longitude, s.width_meters, s.length_meters,
            s.slot_status, s.source, s.zone.price_per_day, s.zone.available_from, s.zone.available_to);

    // proposal_review_status/proposal_photo_url are nullable in the scaffolded model (null for
    // ward-defined slots) but guaranteed set for VENDOR_PROPOSED rows by CK_SidewalkSlots_ProposalCoherent.
    private static readonly System.Linq.Expressions.Expression<Func<ScaffoldedModels.SidewalkSlot, SlotProposalRow>> ToProposalRowExpression =
        s => new SlotProposalRow(
            s.slot_id, s.slot_code, s.latitude, s.longitude,
            s.proposal_review_status!, s.proposal_photo_url!, s.proposal_review_reason, s.created_at);
}
