using System.Globalization;
using StreetBiz.Application.Common.Models;

namespace StreetBiz.Application.DTOs.Finance;

/// <summary>One fee instalment as the vendor sees it (FEE-01).</summary>
public sealed record FeeItemDto(
    long FeeItemId,
    long ContractId,
    string SlotCode,
    string PeriodLabel,
    DateOnly DueDate,
    decimal Amount,
    string ItemStatus,
    DateTime? PaidAt);

/// <summary>A fee schedule revision and its instalments (SYS-03).</summary>
public sealed record FeeScheduleDto(
    long FeeScheduleId,
    long ContractId,
    string SlotCode,
    int Revision,
    decimal TotalAmount,
    DateTime GeneratedAt,
    IReadOnlyList<FeeItemDto> Items);

public static class FinanceMapper
{
    /// <summary>
    /// FeeScheduleItems stores no label: the period is the instalment's position in its schedule
    /// plus the month its payment falls due, which is what the vendor's finance screen shows.
    /// </summary>
    public static string PeriodLabel(int ordinal, int ofCount, DateOnly dueDate) =>
        ofCount <= 1
            ? $"Kỳ duy nhất · Tháng {dueDate.ToString("MM/yyyy", CultureInfo.InvariantCulture)}"
            : $"Kỳ {ordinal}/{ofCount} · Tháng {dueDate.ToString("MM/yyyy", CultureInfo.InvariantCulture)}";

    public static FeeItemDto ToDto(this FeeItemRow row, long contractId, string slotCode) => new(
        row.FeeItemId,
        contractId,
        slotCode,
        PeriodLabel(row.Ordinal, row.OfCount, row.DueDate),
        row.DueDate,
        row.Amount,
        row.ItemStatus,
        // SQL Server returns DATETIME2 as Unspecified; the column is written in UTC, so say so
        // rather than letting the client read it as local wall-clock time.
        row.PaidAt is null ? null : DateTime.SpecifyKind(row.PaidAt.Value, DateTimeKind.Utc));

    public static FeeScheduleDto ToDto(this FeeScheduleRow row, string slotCode) => new(
        row.FeeScheduleId,
        row.ContractId,
        slotCode,
        row.Revision,
        row.TotalAmount,
        DateTime.SpecifyKind(row.GeneratedAt, DateTimeKind.Utc),
        row.Items.Select(item => item.ToDto(row.ContractId, slotCode)).ToList());
}
