using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Infrastructure.Persistence.ScaffoldedModels;

namespace StreetBiz.Infrastructure.Persistence.Repositories;

public sealed class SlotHoldRepository(StreetBizDbContext dbContext) : ISlotHoldRepository
{
    public Task<SlotHoldRow?> GetActiveBySlotAsync(long slotId, DateTime nowUtc, CancellationToken cancellationToken) =>
        dbContext.SlotHolds.AsNoTracking()
            .Where(h => h.slot_id == slotId && h.expires_at > nowUtc)
            .Select(ToRowExpression)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<SlotHoldRow>> ListActiveByRegistrationAsync(
        long registrationId, DateTime nowUtc, CancellationToken cancellationToken) =>
        await dbContext.SlotHolds.AsNoTracking()
            .Where(h => h.registration_id == registrationId && h.expires_at > nowUtc)
            .OrderBy(h => h.expires_at)
            .Select(ToRowExpression)
            .ToListAsync(cancellationToken);

    public async Task UpsertAsync(
        long slotId, long registrationId, DateTime nowUtc, DateTime expiresAt, CancellationToken cancellationToken)
    {
        // One conditional UPDATE covers both "renew my own hold" and "take over an expired one"
        // atomically -- a live hold owned by someone else matches nothing.
        var updated = await dbContext.SlotHolds
            .Where(h => h.slot_id == slotId && (h.registration_id == registrationId || h.expires_at <= nowUtc))
            .ExecuteUpdateAsync(set => set
                .SetProperty(h => h.registration_id, registrationId)
                .SetProperty(h => h.held_at, nowUtc)
                .SetProperty(h => h.expires_at, expiresAt), cancellationToken);
        if (updated > 0)
        {
            return;
        }

        // No row, or a live one owned by someone else. The primary key on slot_id makes two
        // simultaneous inserts safe: the loser (and the live-elsewhere case) gets a key violation.
        dbContext.SlotHolds.Add(new SlotHold
        {
            slot_id = slotId,
            registration_id = registrationId,
            held_at = nowUtc,
            expires_at = expiresAt,
        });
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
    }

    public async Task<bool> ReleaseAsync(long slotId, long registrationId, CancellationToken cancellationToken) =>
        await dbContext.SlotHolds
            .Where(h => h.slot_id == slotId && h.registration_id == registrationId)
            .ExecuteDeleteAsync(cancellationToken) > 0;

    private static readonly System.Linq.Expressions.Expression<Func<SlotHold, SlotHoldRow>> ToRowExpression =
        h => new SlotHoldRow(h.slot_id, h.registration_id, h.held_at, h.expires_at);
}
