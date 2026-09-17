using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.Infrastructure.Persistence.Repositories;

public sealed class DigitalPermitRepository(StreetBizDbContext dbContext) : IDigitalPermitRepository
{
    public Task<PermitValidityRow?> GetValidityByContractAsync(long contractId, CancellationToken cancellationToken) =>
        dbContext.vw_PermitValidities.AsNoTracking()
            .Where(v => v.contract_id == contractId)
            // A contract may have a REVOKED permit plus a later replacement (SIDE-08, BR-19/20):
            // prefer whichever row is not REVOKED; fall back to the most recent REVOKED one.
            .OrderBy(v => v.permit_status == PermitStatuses.Revoked ? 1 : 0)
            .ThenByDescending(v => v.permit_id)
            .Select(v => new PermitValidityRow(
                v.permit_id, v.contract_id, v.qr_payload, v.slot_id, v.vendor_id,
                v.start_date, v.end_date, v.permit_status, v.contract_status, v.effective_status))
            .FirstOrDefaultAsync(cancellationToken);
}
