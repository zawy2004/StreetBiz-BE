namespace StreetBiz.Application.DTOs.Finance;

/// <summary>
/// One instalment as the schedule screen shows it. <c>DaysOverdue</c> is set once the due date has
/// passed unpaid, <c>DaysUntilDue</c> while it is still ahead; both null once paid or cancelled.
/// <c>PayableNow</c> marks the contract's oldest unpaid instalment: instalments are paid in order.
/// </summary>
public sealed record ScheduleItemDto(
    long FeeItemId,
    int Ordinal,
    int OfCount,
    string PeriodLabel,
    DateOnly DueDate,
    decimal Amount,
    string ItemStatus,
    DateTime? PaidAt,
    long? InvoiceId,
    string? InvoiceNumber,
    int? DaysOverdue,
    int? DaysUntilDue,
    bool PayableNow = false);

/// <summary>
/// A rental contract and how far its fee schedule has been paid. Totals and counts leave out
/// instalments cancelled because the contract ended before their period began.
/// </summary>
public sealed record VendorContractFinanceDto(
    long ContractId,
    string SlotCode,
    string? ZoneName,
    string? WardName,
    string? Address,
    DateOnly StartDate,
    DateOnly EndDate,
    string ContractStatus,
    decimal TotalAmount,
    decimal PaidAmount,
    decimal OutstandingAmount,
    int InstalmentCount,
    int PaidCount,
    int OverdueCount,
    ScheduleItemDto? NextDue);

/// <summary>A contract's full schedule, oldest instalment first.</summary>
public sealed record ContractScheduleDto(
    VendorContractFinanceDto Contract,
    IReadOnlyList<ScheduleItemDto> Items);

/// <summary>One instalment with its contract, for the payment screen and its "paid" receipt.</summary>
public sealed record FeeItemDetailDto(
    VendorContractFinanceDto Contract,
    ScheduleItemDto Item);
