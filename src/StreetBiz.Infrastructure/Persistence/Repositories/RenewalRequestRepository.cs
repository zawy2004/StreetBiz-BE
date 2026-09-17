using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Persistence.Repositories;

public sealed class RenewalRequestRepository(StreetBizDbContext dbContext) : IRenewalRequestRepository
{
    public Task<bool> HasOpenAsync(long contractId, CancellationToken cancellationToken) =>
        dbContext.RenewalRequests.AsNoTracking()
            .AnyAsync(r => r.contract_id == contractId && RenewalStatuses.Open.Contains(r.renewal_status), cancellationToken);

    public async Task<long> CreateAsync(long contractId, int requestedTermDays, CancellationToken cancellationToken)
    {
        var entity = new RenewalRequest
        {
            contract_id = contractId,
            requested_term_days = requestedTermDays,
            // renewal_status and created_at are database defaults; new_end_date is set only
            // by WARD-09 approval, never here.
        };

        dbContext.RenewalRequests.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return entity.renewal_id;
    }

    public Task<RenewalRequestRow?> GetByIdAsync(long renewalId, CancellationToken cancellationToken) =>
        dbContext.RenewalRequests.AsNoTracking()
            .Where(r => r.renewal_id == renewalId)
            .Select(ToRowExpression)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<RenewalRequestRow>> ListByContractAsync(long contractId, CancellationToken cancellationToken) =>
        await dbContext.RenewalRequests.AsNoTracking()
            .Where(r => r.contract_id == contractId)
            .OrderByDescending(r => r.created_at)
            .Select(ToRowExpression)
            .ToListAsync(cancellationToken);

    private static readonly System.Linq.Expressions.Expression<Func<RenewalRequest, RenewalRequestRow>> ToRowExpression =
        r => new RenewalRequestRow(
            r.renewal_id, r.contract_id, r.requested_term_days, r.renewal_status,
            r.new_end_date, r.review_decision_reason, r.reviewed_at, r.created_at);
}
