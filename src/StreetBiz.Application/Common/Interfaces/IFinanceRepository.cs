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

    /// <summary>FEE-01: the instalment as its owner sees it, or null when it does not exist or belongs to someone else.</summary>
    Task<FeeItemCheckoutRow?> GetFeeItemForCheckoutAsync(long vendorId, long feeItemId, CancellationToken cancellationToken);

    /// <summary>
    /// FEE-01: opens a PENDING PaymentTransactions row for the instalment. Re-checks ownership
    /// and that the instalment is still outstanding inside the same transaction as the read on
    /// GetFeeItemForCheckoutAsync raced with someone else paying it first.
    /// </summary>
    Task<FinanceCheckoutTransactionRow> CreateFeeCheckoutAsync(
        long vendorId, long feeItemId, string provider, string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>FEE-04: the penalty as its owner sees it, or null.</summary>
    Task<PenaltyCheckoutRow?> GetPenaltyForCheckoutAsync(long vendorId, long penaltyId, CancellationToken cancellationToken);

    /// <summary>FEE-04: opens a PENDING PaymentTransactions row for the penalty.</summary>
    Task<FinanceCheckoutTransactionRow> CreatePenaltyCheckoutAsync(
        long vendorId, long penaltyId, string provider, string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>Records the provider's own reference for a transaction once checkout returns one.</summary>
    Task SetPaymentProviderReferenceAsync(long transactionId, string providerReference, CancellationToken cancellationToken);

    /// <summary>FEE-03: everything the caller's payment receipt prints, or null when it is not theirs.</summary>
    Task<InvoiceDocumentRow?> GetInvoiceDocumentAsync(long vendorId, long invoiceId, CancellationToken cancellationToken);

    /// <summary>
    /// The provider refused to open a checkout for this transaction: mark it FAILED, unless an
    /// earlier attempt already opened a provider order for it (that page may still be paid).
    /// </summary>
    Task AbandonUnopenedCheckoutAsync(long transactionId, CancellationToken cancellationToken);

    /// <summary>
    /// SYS-04 dispatch: which purpose (RENTAL_FEE/PENALTY/ORDER) a transaction belongs to, looked
    /// up the same way <see cref="ApplyPaymentCallbackAsync"/> will — provider reference first,
    /// idempotency key second. Read-only, so the caller can route before anything is written; null
    /// when nothing matches either key.
    /// </summary>
    Task<string?> FindPaymentPurposeAsync(
        string? providerReference, string? idempotencyKey, CancellationToken cancellationToken);

    /// <summary>
    /// SYS-04 for RENTAL_FEE/PENALTY transactions: verifies, applies idempotently, and on success
    /// marks the instalment/penalty PAID and issues its invoice (SYS-05) in the same transaction.
    /// Every callback is recorded in PaymentCallbackEvents whether or not it matched or verified.
    /// </summary>
    Task<FinanceCallbackMutationResult> ApplyPaymentCallbackAsync(
        PaymentCallbackData callback, CancellationToken cancellationToken);

    /// <summary>
    /// Development-only stand-in for a real signed callback: applies the same success path as
    /// <see cref="ApplyPaymentCallbackAsync"/> directly, for testing without a provider sandbox.
    /// </summary>
    Task<FinanceCallbackMutationResult> ConfirmSandboxSuccessAsync(
        long vendorId, long transactionId, CancellationToken cancellationToken);

    /// <summary>A fee/penalty payment this vendor opened, or null if it is not theirs.</summary>
    Task<FinancePaymentStateRow?> GetVendorPaymentAsync(
        long vendorId, long transactionId, CancellationToken cancellationToken) =>
        Task.FromResult<FinancePaymentStateRow?>(null);

    /// <summary>FEE-03: the vendor's invoices, most recent first.</summary>
    Task<IReadOnlyList<InvoiceRow>> ListInvoicesAsync(long vendorId, CancellationToken cancellationToken);

    /// <summary>FEE-03: one invoice, or null when it does not exist or belongs to someone else.</summary>
    Task<InvoiceRow?> GetInvoiceAsync(long vendorId, long invoiceId, CancellationToken cancellationToken);

    /// <summary>
    /// FEE-02/SYS-06, run on a schedule (see FeeReminderHostedService): moves every PENDING
    /// instalment whose due date has passed to OVERDUE (one notification each, at the moment of
    /// transition), and sends a reminder for every PENDING instalment due within
    /// <see cref="ReminderWindowDays"/> days that has not already been reminded today. Idempotent
    /// per day — safe to run more than once without re-notifying the same instalment twice in one
    /// day — because the schema has no dedicated "reminder sent" flag to add without a reviewed
    /// migration (see README's migration policy).
    /// </summary>
    Task<FeeReminderSweepResult> RunFeeReminderSweepAsync(DateOnly today, CancellationToken cancellationToken);

    /// <summary>FinanceHome top card: outstanding fee/penalty totals, overdue count, next due date.</summary>
    Task<FinanceSummaryRow> GetSummaryAsync(long vendorId, CancellationToken cancellationToken);

    /// <summary>FinanceHome's "Phí thuê ô" tab: every instalment across the vendor's contracts (current schedule only), optionally filtered by status.</summary>
    Task<IReadOnlyList<FeeItemListRow>> ListFeeItemsAsync(long vendorId, string? status, CancellationToken cancellationToken);

    /// <summary>FinanceHome's "Biên bản phạt" tab: every penalty against the vendor, optionally filtered by status.</summary>
    Task<IReadOnlyList<PenaltyListRow>> ListPenaltiesAsync(long vendorId, string? status, CancellationToken cancellationToken);

    /// <summary>FEE-05: the vendor's payment attempts (fee and penalty purposes only — Orders keeps its own), most recent first.</summary>
    Task<IReadOnlyList<PaymentTransactionRow>> ListPaymentTransactionsAsync(long vendorId, CancellationToken cancellationToken);

    /// <summary>FEE-05: violations recorded against the vendor, most recent first.</summary>
    Task<IReadOnlyList<VendorViolationRow>> ListVendorViolationsAsync(long vendorId, CancellationToken cancellationToken);
}
