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

    /// <summary>The ward's zones; a slot counts as rented while a contract is ACTIVE and in force on <paramref name="today"/>.</summary>
    Task<IReadOnlyList<WardZoneRow>> ListZonesAsync(int wardUnitId, DateOnly today, CancellationToken cancellationToken);

    /// <summary>When each contract's vendor was last sent a ward debt reminder, if ever.</summary>
    Task<IReadOnlyDictionary<long, DateTime>> GetLastDebtRemindersAsync(
        IReadOnlyCollection<long> contractIds, CancellationToken cancellationToken);

    /// <summary>
    /// Writes the in-app reminder and the audit entry for it, unless one was already sent at or
    /// after <paramref name="notBeforeUtc"/>: checked and written in one serializable transaction,
    /// so two officers pressing at once send one. Returns the earlier reminder's time when it
    /// exists (nothing written), or null when this one was sent.
    /// </summary>
    Task<DateTime?> SendDebtReminderAsync(
        long contractId,
        long vendorUserId,
        long actorUserId,
        string title,
        string body,
        DateTime now,
        DateTime notBeforeUtc,
        CancellationToken cancellationToken);

    /// <summary>The ward's payments owed back to vendors (REFUND_REQUIRED) and those already refunded, newest first.</summary>
    Task<IReadOnlyList<WardRefundRow>> ListRefundsAsync(int wardUnitId, CancellationToken cancellationToken);

    /// <summary>
    /// Records that the ward paid a REFUND_REQUIRED payment back (REFUNDED), with an audit entry
    /// and a notice to the vendor. Null when the payment is not in this ward; refused (422) when
    /// it is not waiting for a refund.
    /// </summary>
    Task<WardRefundRow?> MarkRefundedAsync(
        int wardUnitId, long transactionId, long actorUserId, string? note, DateTime now, CancellationToken cancellationToken);
}
