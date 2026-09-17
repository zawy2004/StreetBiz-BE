using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.AdministrativeUnits;

namespace StreetBiz.Infrastructure.Persistence.Repositories;

public sealed class AdministrativeUnitRepository(StreetBizDbContext dbContext)
    : IAdministrativeUnitRepository
{
    public async Task<IReadOnlyList<WardDto>> ListWardsAsync(CancellationToken cancellationToken)
    {
        var wards = await dbContext.AdministrativeUnits.AsNoTracking()
            .Where(u => u.unit_type == AdministrativeUnitTypes.Ward)
            .OrderBy(u => u.unit_name)
            .Select(u => new WardDto(
                u.unit_id,
                u.unit_name,
                u.parent_unit == null ? null : u.parent_unit.unit_name))
            .ToListAsync(cancellationToken);

        return wards;
    }

    public Task<bool> IsWardAsync(int unitId, CancellationToken cancellationToken) =>
        dbContext.AdministrativeUnits.AsNoTracking()
            .AnyAsync(u => u.unit_id == unitId && u.unit_type == AdministrativeUnitTypes.Ward, cancellationToken);
}
