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
