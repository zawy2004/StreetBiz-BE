using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Interfaces;

namespace StreetBiz.Infrastructure.Persistence.Repositories;

public sealed class VendorRepository(StreetBizDbContext dbContext) : IVendorRepository
{
    public async Task<long?> GetVendorIdByUserAsync(long userId, CancellationToken cancellationToken)
    {
        var vendor = await dbContext.Vendors.AsNoTracking()
            .FirstOrDefaultAsync(v => v.user_id == userId, cancellationToken);
        return vendor?.vendor_id;
    }
}
