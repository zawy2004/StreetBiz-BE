using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Persistence.Repositories;

public sealed class RentalContractRepository(StreetBizDbContext dbContext) : IRentalContractRepository
{
    public Task<bool> HasActiveAdjacentContractAsync(long registrationId, CancellationToken cancellationToken) =>
        dbContext.RentalContracts.AsNoTracking()
            .AnyAsync(c => c.contract_status == ContractStatuses.Active
                        && c.application.registration_id == registrationId
                        && c.application.application_method == ApplicationMethods.AutoAdjacent,
                cancellationToken);

    public async Task<IReadOnlyList<RentalContractRow>> ListByVendorAsync(
        long vendorId, string? status, CancellationToken cancellationToken)
    {
        var query = dbContext.RentalContracts.AsNoTracking().Where(c => c.vendor_id == vendorId);
        if (status is not null)
        {
            query = query.Where(c => c.contract_status == status);
        }

        return await query
            .OrderByDescending(c => c.created_at)
            .Select(ToRowExpression)
            .ToListAsync(cancellationToken);
    }

    public Task<RentalContractRow?> GetByIdAsync(long contractId, CancellationToken cancellationToken) =>
        dbContext.RentalContracts.AsNoTracking()
            .Where(c => c.contract_id == contractId)
            .Select(ToRowExpression)
            .FirstOrDefaultAsync(cancellationToken);

    private static readonly System.Linq.Expressions.Expression<Func<RentalContract, RentalContractRow>> ToRowExpression =
        c => new RentalContractRow(
            c.contract_id, c.application_id, c.slot_id, c.slot.slot_code, c.slot.zone.zone_name, c.vendor_id,
            c.start_date, c.end_date, c.contract_status, c.cancelled_by, c.cancellation_reason,
            c.cancelled_at, c.created_at, c.updated_at);
}
