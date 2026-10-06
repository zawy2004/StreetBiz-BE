using StreetBiz.Application.Common.Models;

namespace StreetBiz.Application.Common.Interfaces;

/// <summary>
/// WARD-14 collections: who owes the ward, how well fees are paid on time, and the reminders an
/// officer sends. Every query is scoped to one ward through the slot's pricing zone.
/// </summary>
public interface IWardCollectionRepository
{
    /// <summary>Unpaid instalments (PENDING or OVERDUE) of current schedules, any due date.</summary>
    Task<IReadOnlyList<WardUnpaidItemRow>> ListUnpaidFeeItemsAsync(int wardUnitId, CancellationToken cancellationToken);

    /// <summary>Instalments due in [dueFrom, dueTo], or paid in [paidFromUtc, paidToExclusiveUtc).</summary>
    Task<IReadOnlyList<WardFeeItemRow>> ListFeeActivityAsync(
        int wardUnitId,
        DateOnly dueFrom,
        DateOnly dueTo,
        DateTime paidFromUtc,
        DateTime paidToExclusiveUtc,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<WardPenaltyPaymentRow>> ListPenaltyPaymentsAsync(
        int wardUnitId, DateTime fromUtc, DateTime toExclusiveUtc, CancellationToken cancellationToken);

    Task<IReadOnlyList<WardZoneRow>> ListZonesAsync(int wardUnitId, CancellationToken cancellationToken);

    /// <summary>When each contract's vendor was last sent a ward debt reminder, if ever.</summary>
    Task<IReadOnlyDictionary<long, DateTime>> GetLastDebtRemindersAsync(
        IReadOnlyCollection<long> contractIds, CancellationToken cancellationToken);

    /// <summary>Writes the in-app reminder and the audit entry for it, in one save.</summary>
    Task SendDebtReminderAsync(
        long contractId,
        long vendorUserId,
        long actorUserId,
        string title,
        string body,
        DateTime now,
        CancellationToken cancellationToken);
}
