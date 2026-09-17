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

    public async Task<bool> HasOutstandingDebtAsync(long contractId, CancellationToken cancellationToken)
    {
        var hasOverdueFee = await dbContext.FeeScheduleItems.AsNoTracking()
            .AnyAsync(i => i.fee_schedule.contract_id == contractId && i.item_status == DebtStatuses.FeeItemOverdue,
                cancellationToken);
        if (hasOverdueFee)
        {
            return true;
        }

        return await dbContext.Penalties.AsNoTracking()
            .AnyAsync(p => p.violation.contract_id == contractId && p.penalty_status == DebtStatuses.PenaltyUnpaid,
                cancellationToken);
    }

    public async Task CancelAsync(long contractId, long cancelledByUserId, string? reason, CancellationToken cancellationToken)
    {
        var contract = await dbContext.RentalContracts
            .Include(c => c.slot)
            .FirstOrDefaultAsync(c => c.contract_id == contractId, cancellationToken);
        if (contract is null)
        {
            return;
        }

        contract.contract_status = ContractStatuses.Cancelled;
        contract.cancelled_by = cancelledByUserId;
        contract.cancellation_reason = reason;
        contract.cancelled_at = DateTime.UtcNow;
        contract.slot.slot_status = SlotStatuses.Available;

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            // TR_RentalContracts_NoCancelWithDebt is the safety net behind the handler's own
            // HasOutstandingDebtAsync pre-check, for the race where a fee turns OVERDUE between
            // the check and this write.
            var translated = SqlErrorTranslator.TryTranslate(ex);
            if (translated is not null)
            {
                throw translated;
            }

            throw;
        }
    }

    private static readonly System.Linq.Expressions.Expression<Func<RentalContract, RentalContractRow>> ToRowExpression =
        c => new RentalContractRow(
            c.contract_id, c.application_id, c.slot_id, c.slot.slot_code, c.slot.zone.zone_name, c.vendor_id,
            c.start_date, c.end_date, c.contract_status, c.cancelled_by, c.cancellation_reason,
            c.cancelled_at, c.created_at, c.updated_at);
}
