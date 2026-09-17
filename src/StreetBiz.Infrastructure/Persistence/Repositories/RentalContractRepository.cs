using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.Infrastructure.Persistence.Repositories;

public sealed class RentalContractRepository(StreetBizDbContext dbContext) : IRentalContractRepository
{
    public Task<bool> HasActiveAdjacentContractAsync(long registrationId, CancellationToken cancellationToken) =>
        dbContext.RentalContracts.AsNoTracking()
            .AnyAsync(c => c.contract_status == ContractStatuses.Active
                        && c.application.registration_id == registrationId
                        && c.application.application_method == ApplicationMethods.AutoAdjacent,
                cancellationToken);
}
