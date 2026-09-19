using StreetBiz.Application.Common.Models;

namespace StreetBiz.Application.Common.Interfaces;

/// <summary>
/// Slot holds: at most one row per slot (the table's primary key). Nothing sweeps expired rows --
/// every read takes the current time and ignores rows whose <c>ExpiresAt</c> has passed.
/// </summary>
public interface ISlotHoldRepository
{
    /// <summary>The live (unexpired) hold on a slot, whoever owns it.</summary>
    Task<SlotHoldRow?> GetActiveBySlotAsync(long slotId, DateTime nowUtc, CancellationToken cancellationToken);

    Task<IReadOnlyList<SlotHoldRow>> ListActiveByRegistrationAsync(
        long registrationId, DateTime nowUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Takes or renews the hold. Succeeds when the slot has no row, an expired row, or a row already
    /// owned by <paramref name="registrationId"/>; throws ConflictException when a live hold belongs to
    /// someone else (including losing a race for the same slot).
    /// </summary>
    Task UpsertAsync(
        long slotId, long registrationId, DateTime nowUtc, DateTime expiresAt, CancellationToken cancellationToken);

    /// <summary>Deletes the caller's own hold. Returns false when the registration held nothing on the slot.</summary>
    Task<bool> ReleaseAsync(long slotId, long registrationId, CancellationToken cancellationToken);
}
