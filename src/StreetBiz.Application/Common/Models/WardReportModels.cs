namespace StreetBiz.Application.Common.Models;

/// <summary>WARD-14: fee/penalty collection totals for a ward over a period, plus its most recent violations.</summary>
public sealed record CollectionReportRow(
    decimal FeeCollected,
    decimal FeePending,
    decimal FeeOverdue,
    decimal PenaltyCollected,
    decimal PenaltyPending,
    int InvoiceCount,
    IReadOnlyList<RecentViolationRow> RecentViolations);

public sealed record RecentViolationRow(
    long ViolationId,
    string ViolationType,
    string ViolationLabel,
    string? VendorName,
    string? SlotCode,
    decimal? PenaltyAmount,
    string? PenaltyStatus,
    DateTime RecordedAt);

/// <summary>WARD-15: ward-wide operational snapshot. Occupancy is computed in the DTO mapper, not stored here.</summary>
public sealed record WardDashboardRow(
    int SlotTotal,
    int SlotRented,
    int PendingRegistrations,
    int PendingApplications,
    int ActiveContracts,
    decimal RevenueCollected,
    decimal OutstandingDebt,
    int OpenViolations);

/// <summary>
/// What the AIC-08 summary needs beyond the WARD-14/15 totals: how the period's violations break
/// down, how many paid instalments arrived on time, and who owes the most right now.
/// </summary>
public sealed record ReportPeriodFacts(
    int ViolationCount,
    int PenaltiesIssued,
    decimal PenaltiesIssuedAmount,
    IReadOnlyList<ViolationTypeCountRow> TopViolationTypes,
    int FeeItemsPaid,
    int FeeItemsPaidOnTime,
    int OverdueItemCount,
    int OverdueVendorCount,
    OverdueDebtorRow? LargestDebtor);

public sealed record ViolationTypeCountRow(string ViolationType, string Label, int Count);

public sealed record OverdueDebtorRow(string VendorName, decimal Amount);
