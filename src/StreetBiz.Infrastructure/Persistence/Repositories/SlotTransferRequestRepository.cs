using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Persistence.Repositories;

public sealed class SlotTransferRequestRepository(StreetBizDbContext dbContext) : ISlotTransferRequestRepository
{
    public Task<bool> HasOpenForContractAsync(long contractId, CancellationToken cancellationToken) =>
        dbContext.SlotTransferRequests.AsNoTracking()
            .AnyAsync(t => t.contract_id == contractId && TransferStatuses.Open.Contains(t.transfer_status),
                cancellationToken);

    public async Task<long> CreateAsync(long contractId, long fromVendorId, long toVendorId, CancellationToken cancellationToken)
    {
        var entity = new SlotTransferRequest
        {
            contract_id = contractId,
            from_vendor_id = fromVendorId,
            to_vendor_id = toVendorId,
            transfer_status = TransferStatuses.Pending,
            // initiated_at is a database default -- never set here.
        };

        dbContext.SlotTransferRequests.Add(entity);
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

        return entity.transfer_id;
    }

    public Task<SlotTransferRequestRow?> GetByIdAsync(long transferId, CancellationToken cancellationToken) =>
        dbContext.SlotTransferRequests.AsNoTracking()
            .Where(t => t.transfer_id == transferId)
            .Select(ToRowExpression)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<SlotTransferRequestRow>> ListByVendorAsync(long vendorId, string direction, CancellationToken cancellationToken)
    {
        var query = direction == TransferDirections.Incoming
            ? dbContext.SlotTransferRequests.AsNoTracking().Where(t => t.to_vendor_id == vendorId)
            : dbContext.SlotTransferRequests.AsNoTracking().Where(t => t.from_vendor_id == vendorId);

        return await query
            .OrderByDescending(t => t.initiated_at)
            .Select(ToRowExpression)
            .ToListAsync(cancellationToken);
    }

    public async Task SetStatusAsync(long transferId, string status, DateTime? acceptedAt, CancellationToken cancellationToken)
    {
        var entity = await dbContext.SlotTransferRequests
            .FirstOrDefaultAsync(t => t.transfer_id == transferId, cancellationToken);
        if (entity is null)
        {
            return;
        }

        entity.transfer_status = status;
        if (acceptedAt is not null)
        {
            entity.accepted_at = acceptedAt;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static readonly Expression<Func<SlotTransferRequest, SlotTransferRequestRow>> ToRowExpression =
        t => new SlotTransferRequestRow(
            t.transfer_id, t.contract_id, t.from_vendor_id, t.to_vendor_id, t.transfer_status,
            t.initiated_at, t.accepted_at, t.review_decision_reason, t.reviewed_at,
            t.contract.slot.slot_code, t.contract.slot.zone.zone_name,
            t.contract.start_date, t.contract.end_date);
}
