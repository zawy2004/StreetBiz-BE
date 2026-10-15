namespace StreetBiz.Application.DTOs.Finance;

/// <summary>
/// One contract with overdue rental fees: who, where, how much, how late, and whether the ward
/// has already reminded them today.
/// </summary>
public sealed record WardDebtorDto(
    long ContractId,
    string VendorName,
    string? VendorPhone,
    string? BusinessName,
    string SlotCode,
    string ZoneName,
    int OverdueCount,
    decimal OverdueAmount,
    decimal UpcomingAmount,
    DateOnly OldestDueDate,
    int DaysOverdue,
    DateTime? LastRemindedAt,
    bool RemindedToday);

public sealed record ZoneCollectionDto(
    int ZoneId,
    string ZoneName,
    int SlotCount,
    int RentedSlots,
    decimal FeeCollected,
    decimal Outstanding);

/// <summary>
/// How well rental fees falling due in a period were paid. <c>OnTimeRate</c> is paid-on-time
/// over everything that fell due (null when nothing did); "on time" means by the end of the due
/// date in Vietnam time.
/// </summary>
public sealed record CollectionPerformanceDto(
    DateOnly From,
    DateOnly To,
    decimal FeeDue,
    int DueCount,
    decimal DueCollected,
    int PaidOnTimeCount,
    int PaidLateCount,
    int UnpaidCount,
    double? OnTimeRate,
    IReadOnlyList<ZoneCollectionDto> ByZone);

/// <summary>One calendar month (Vietnam time) of collections.</summary>
public sealed record MonthlyCollectionDto(
    int Year,
    int Month,
    decimal FeeCollected,
    decimal PenaltyCollected,
    decimal FeeDue,
    decimal FeeDuePaidOnTime);

public sealed record DebtReminderDto(long ContractId, DateTime RemindedAt);

/// <summary>
/// One age band of overdue rental fees: <c>MinDays</c> to <c>MaxDays</c> days past due (no upper
/// bound when null). <c>Households</c> counts contracts, so a household is in the band of each
/// instalment it owes.
/// </summary>
public sealed record AgingBucketDto(
    string Key,
    string Label,
    int MinDays,
    int? MaxDays,
    int Instalments,
    int Households,
    decimal Amount);

/// <summary>
/// WARD-14 receivables as of today: overdue fees by age, what falls due in the next 30 days, and
/// payments received twice that the ward owes back.
/// </summary>
public sealed record ReceivablesDto(
    DateOnly AsOf,
    decimal OverdueAmount,
    IReadOnlyList<AgingBucketDto> Aging,
    int DueNext30Count,
    decimal DueNext30Amount,
    int RefundsPending,
    decimal RefundsPendingAmount);

/// <summary>A payment owed back to a vendor (REFUND_REQUIRED) or already paid back (REFUNDED).</summary>
public sealed record WardRefundDto(
    long TransactionId,
    string Kind,
    string VendorName,
    string? VendorPhone,
    string? SlotCode,
    string Label,
    string Provider,
    string? ProviderReference,
    decimal Amount,
    DateTime ReceivedAt,
    string Status,
    DateTime? RefundedAt);
