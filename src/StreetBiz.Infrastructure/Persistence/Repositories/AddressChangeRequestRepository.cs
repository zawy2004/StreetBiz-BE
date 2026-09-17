using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Persistence.Repositories;

public sealed class AddressChangeRequestRepository(StreetBizDbContext dbContext) : IAddressChangeRequestRepository
{
    public Task<bool> HasOpenAsync(long registrationId, CancellationToken cancellationToken) =>
        dbContext.AddressChangeRequests.AsNoTracking()
            .AnyAsync(r => r.registration_id == registrationId && AddressChangeStatuses.Open.Contains(r.change_status),
                cancellationToken);

    public async Task<long> CreateAsync(long registrationId, NewAddressChangeRequest data, CancellationToken cancellationToken)
    {
        var entity = new AddressChangeRequest
        {
            registration_id = registrationId,
            new_address = data.NewAddress,
            new_latitude = data.NewLatitude,
            new_longitude = data.NewLongitude,
            released_contract_id = data.ReleasedContractId,
            requested_new_slot_id = data.RequestedNewSlotId,
            // change_status and created_at are database defaults — never set here.
        };

        dbContext.AddressChangeRequests.Add(entity);
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

        return entity.address_change_id;
    }

    public Task<AddressChangeRequestRow?> GetByIdAsync(long addressChangeId, CancellationToken cancellationToken) =>
        dbContext.AddressChangeRequests.AsNoTracking()
            .Where(r => r.address_change_id == addressChangeId)
            .Select(ToRowExpression)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<AddressChangeRequestRow>> ListByVendorAsync(long vendorId, CancellationToken cancellationToken) =>
        await dbContext.AddressChangeRequests.AsNoTracking()
            .Where(r => r.registration.vendor_id == vendorId)
            .OrderByDescending(r => r.created_at)
            .Select(ToRowExpression)
            .ToListAsync(cancellationToken);

    private static readonly System.Linq.Expressions.Expression<Func<AddressChangeRequest, AddressChangeRequestRow>> ToRowExpression =
        r => new AddressChangeRequestRow(
            r.address_change_id, r.registration_id, r.new_address, r.new_latitude, r.new_longitude,
            r.released_contract_id, r.requested_new_slot_id, r.change_status,
            r.conflict_resolution_note, r.reviewed_at, r.created_at);
}
