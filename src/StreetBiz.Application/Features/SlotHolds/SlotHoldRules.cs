using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.Application.Features.SlotHolds;

/// <summary>Rules shared by everything that must respect another vendor's hold on a slot.</summary>
public static class SlotHoldRules
{
    /// <summary>
    /// Returns the live hold on the slot when it belongs to <paramref name="registrationId"/> (or null
    /// when there is none) and throws when someone else holds it.
    /// </summary>
    public static async Task<SlotHoldRow?> RequireNotHeldByAnotherAsync(
        ISlotHoldRepository holds, DateTime nowUtc, long slotId, long registrationId, CancellationToken cancellationToken)
    {
        var existing = await holds.GetActiveBySlotAsync(slotId, nowUtc, cancellationToken);
        if (existing is not null && existing.RegistrationId != registrationId)
        {
            throw new ConflictException(SideMessages.SlotHeldByAnother);
        }

        return existing;
    }
}
