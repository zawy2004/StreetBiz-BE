using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Persistence.Repositories;

public sealed class RentalApplicationRepository(StreetBizDbContext dbContext) : IRentalApplicationRepository
{
    public Task<bool> HasOpenApplicationForSlotAsync(long slotId, CancellationToken cancellationToken) =>
        dbContext.RentalApplications.AsNoTracking()
            .AnyAsync(a => a.slot_id == slotId && ApplicationStatuses.Open.Contains(a.application_status), cancellationToken);

    public async Task<long> CreateAsync(
        long registrationId, long slotId, string applicationMethod, int requestedTermDays,
        CancellationToken cancellationToken)
    {
        var entity = new RentalApplication
        {
            registration_id = registrationId,
            slot_id = slotId,
            application_method = applicationMethod,
            requested_term_days = requestedTermDays,
            // application_status and created_at are database defaults — never set here.
        };

        dbContext.RentalApplications.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return entity.application_id;
    }

    public Task<RentalApplicationRow?> GetByIdAsync(long applicationId, CancellationToken cancellationToken) =>
        dbContext.RentalApplications.AsNoTracking()
            .Where(a => a.application_id == applicationId)
            .Select(a => new RentalApplicationRow(
                a.application_id, a.registration_id, a.slot_id, a.application_method,
                a.requested_term_days, a.application_status, a.review_decision_reason,
                a.reviewed_at, a.created_at))
            .FirstOrDefaultAsync(cancellationToken);
}
