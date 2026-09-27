using StreetBiz.Application.Common.Models;

namespace StreetBiz.Application.DTOs.Finance;

/// <summary>WARD-14.</summary>
public sealed record CollectionReportDto(
    DateOnly From,
    DateOnly To,
    decimal FeeCollected,
    decimal FeePending,
    decimal FeeOverdue,
    decimal PenaltyCollected,
    decimal PenaltyPending,
    int InvoiceCount,
    IReadOnlyList<RecentViolationDto> RecentViolations);

public sealed record RecentViolationDto(
    long ViolationId,
    string ViolationType,
    string ViolationLabel,
    string? VendorName,
    string? SlotCode,
    decimal? PenaltyAmount,
    string? PenaltyStatus,
    DateTime RecordedAt);

/// <summary>WARD-15.</summary>
public sealed record WardDashboardDto(
    int SlotTotal,
    int SlotRented,
    int OccupancyPercent,
    int PendingRegistrations,
    int PendingApplications,
    int ActiveContracts,
    decimal RevenueCollected,
    decimal OutstandingDebt,
    int OpenViolations);

public static class WardReportMapper
{
    public static CollectionReportDto ToDto(this CollectionReportRow row, DateOnly from, DateOnly to) => new(
        from,
        to,
        row.FeeCollected,
        row.FeePending,
        row.FeeOverdue,
        row.PenaltyCollected,
        row.PenaltyPending,
        row.InvoiceCount,
        row.RecentViolations.Select(v => new RecentViolationDto(
            v.ViolationId, v.ViolationType, v.ViolationLabel, v.VendorName, v.SlotCode,
            v.PenaltyAmount, v.PenaltyStatus, DateTime.SpecifyKind(v.RecordedAt, DateTimeKind.Utc))).ToList());

    public static WardDashboardDto ToDto(this WardDashboardRow row) => new(
        row.SlotTotal,
        row.SlotRented,
        row.SlotTotal == 0 ? 0 : (int)Math.Round(100m * row.SlotRented / row.SlotTotal, 0, MidpointRounding.AwayFromZero),
        row.PendingRegistrations,
        row.PendingApplications,
        row.ActiveContracts,
        row.RevenueCollected,
        row.OutstandingDebt,
        row.OpenViolations);
}
