using StreetBiz.Application.Common.Models;

namespace StreetBiz.Application.Common.Interfaces;

/// <summary>
/// Fee schedules, payments, invoices and penalties (FEE-01..05, SYS-03..05). Money in this
/// module belongs to the ward, so every write is audited and every read is scoped to its owner.
/// </summary>
public interface IFinanceRepository
{
    /// <summary>
    /// SYS-03 input: the contract, its applied-for term, and the zone prices behind its slot.
    /// Null when the contract does not exist.
    /// </summary>
    Task<FeeScheduleContextRow?> GetFeeScheduleContextAsync(long contractId, CancellationToken cancellationToken);

    /// <summary>
    /// SYS-03 write. Closes the contract's current schedule with superseded_at and inserts the
    /// next revision with its instalments, in one transaction. Never edits an existing schedule:
    /// UQ_FeeSchedules_CurrentPerContract allows exactly one open revision, and the superseded
    /// rows are the audit trail (BR-18).
    /// </summary>
    Task<FeeScheduleRow> ReplaceFeeScheduleAsync(
        long contractId,
        long actorUserId,
        decimal total,
        IReadOnlyList<FeeInstalment> instalments,
        CancellationToken cancellationToken);

    /// <summary>The contract's open (non-superseded) schedule with its instalments, or null.</summary>
    Task<FeeScheduleRow?> GetCurrentFeeScheduleAsync(long contractId, CancellationToken cancellationToken);
}
