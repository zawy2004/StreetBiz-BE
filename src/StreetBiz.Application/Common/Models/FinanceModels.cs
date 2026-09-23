namespace StreetBiz.Application.Common.Models;

/// <summary>One instalment of a generated fee schedule (SYS-03).</summary>
public sealed record FeeInstalment(
    int Ordinal,
    DateOnly DueDate,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    decimal Amount);

/// <summary>A whole plan: the contract total and the instalments that add up to it exactly.</summary>
public sealed record FeePlan(decimal Total, IReadOnlyList<FeeInstalment> Instalments);

/// <summary>
/// Everything SYS-03 needs to price a contract: the term the vendor applied for and the zone
/// prices its slot sits in. <c>TermDays</c> is the application's requested_term_days, not a
/// difference between dates, so the schedule always matches the SIDE-02 quote for that term.
/// </summary>
public sealed record FeeScheduleContextRow(
    long ContractId,
    long SlotId,
    string SlotCode,
    int ZoneId,
    string ZoneName,
    long VendorId,
    long VendorUserId,
    DateOnly StartDate,
    DateOnly EndDate,
    string ContractStatus,
    int TermDays,
    decimal PricePerDay,
    IReadOnlyList<FeeComponentRow> Components);

/// <summary>A fee schedule revision and its instalments.</summary>
public sealed record FeeScheduleRow(
    long FeeScheduleId,
    long ContractId,
    int Revision,
    decimal TotalAmount,
    DateTime GeneratedAt,
    DateTime? SupersededAt,
    IReadOnlyList<FeeItemRow> Items);

/// <summary>
/// One row of FeeScheduleItems. <c>Ordinal</c> is not stored: it is the position by due date
/// within the schedule, which is what the vendor-facing period label is built from.
/// </summary>
public sealed record FeeItemRow(
    long FeeItemId,
    long FeeScheduleId,
    int Ordinal,
    int OfCount,
    DateOnly DueDate,
    decimal Amount,
    string ItemStatus,
    DateTime? PaidAt);

/// <summary>A fee instalment as seen for FEE-01 checkout: who owns it, what to call it, whether it can still be paid.</summary>
public sealed record FeeItemCheckoutRow(
    long FeeItemId,
    long VendorId,
    long VendorUserId,
    string SlotCode,
    int Ordinal,
    int OfCount,
    DateOnly DueDate,
    decimal Amount,
    string ItemStatus);

/// <summary>A penalty as seen for FEE-04 checkout.</summary>
public sealed record PenaltyCheckoutRow(
    long PenaltyId,
    long VendorId,
    long VendorUserId,
    string ViolationLabel,
    string? SlotCode,
    decimal Amount,
    string PenaltyStatus);

/// <summary>The PENDING transaction FEE-01/FEE-04 just opened, ready to hand to the payment gateway.</summary>
public sealed record FinanceCheckoutTransactionRow(
    long TransactionId,
    string IdempotencyKey,
    string Provider,
    decimal Amount);

/// <summary>
/// SYS-04 outcome for a RENTAL_FEE or PENALTY callback. No order fields: this purpose never
/// touches Orders. Reuses <see cref="PaymentCallbackOutcome"/> from the Commerce module — the
/// four outcomes (Applied/Duplicate/Unmatched/Rejected) are purpose-agnostic already.
/// </summary>
public sealed record FinanceCallbackMutationResult(
    PaymentCallbackOutcome Outcome,
    long CallbackEventId,
    long? TransactionId);

/// <summary>One invoice as the vendor sees it (FEE-03). <c>Kind</c> is FEE or PENALTY — never both (CK_Invoices_ExactlyOneSource).</summary>
public sealed record InvoiceRow(
    long InvoiceId,
    string InvoiceNumber,
    string Kind,
    decimal Amount,
    DateTime IssuedAt,
    long? FeeItemId,
    long? PenaltyId,
    string? PeriodLabel,
    string? SlotCode,
    string? ViolationLabel,
    string? PaymentProvider,
    DateTime? PaidAt);
