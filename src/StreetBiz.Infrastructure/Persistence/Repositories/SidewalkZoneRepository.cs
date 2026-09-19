using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;

namespace StreetBiz.Infrastructure.Persistence.Repositories;

public sealed class SidewalkZoneRepository(StreetBizDbContext dbContext) : ISidewalkZoneRepository
{
    public async Task<ZoneDetailRow?> GetDetailAsync(int zoneId, CancellationToken cancellationToken)
    {
        var zone = await dbContext.PricingZones.AsNoTracking()
            .Where(z => z.zone_id == zoneId)
            .Select(z => new
            {
                z.zone_id, z.zone_name, z.zone_code, z.regulation_ref, z.segment_from, z.segment_to,
                z.application_deadline, z.ward_unit_id, WardName = z.AdministrativeUnit!.unit_name,
                ContactName = z.AdministrativeUnit.contact_name, ContactPhone = z.AdministrativeUnit.contact_phone,
                z.price_per_day, z.available_from, z.available_to,
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (zone is null)
        {
            return null;
        }

        var features = await dbContext.StreetFeatures.AsNoTracking()
            .Where(f => f.zone_id == zoneId)
            .OrderBy(f => f.feature_id)
            .Select(f => new StreetFeatureRow(
                f.feature_id, f.feature_type, f.label, f.latitude, f.longitude, f.blocks_business, f.note))
            .ToListAsync(cancellationToken);

        return new ZoneDetailRow(
            zone.zone_id, zone.zone_name, zone.zone_code, zone.regulation_ref, zone.segment_from, zone.segment_to,
            zone.application_deadline, zone.ward_unit_id, zone.WardName, zone.ContactName, zone.ContactPhone,
            zone.price_per_day, zone.available_from, zone.available_to,
            await ListFeeComponentsAsync(zoneId, cancellationToken), features);
    }

    public async Task<IReadOnlyList<FeeComponentRow>> ListFeeComponentsAsync(int zoneId, CancellationToken cancellationToken) =>
        await dbContext.ZoneFeeComponents.AsNoTracking()
            .Where(c => c.zone_id == zoneId)
            .OrderBy(c => c.sort_order).ThenBy(c => c.component_id)
            .Select(c => new FeeComponentRow(c.component_id, c.component_name, c.calc_basis, c.unit_amount, c.sort_order))
            .ToListAsync(cancellationToken);
}
