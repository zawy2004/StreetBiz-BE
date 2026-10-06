using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Finance;

namespace StreetBiz.Application.Features.Finance.WardReports;

/// <summary>
/// WARD-14 collections, computed in memory from small, ward-scoped row sets. Pure, so every
/// figure the ward sees is unit-tested; one ward's fee items are a few hundred rows at most.
///
/// Day boundaries are Vietnamese calendar days (<see cref="BusinessCalendar"/>), like every other
/// finance figure: an instalment paid at 23:30 on its due date in Đà Nẵng is on time, even though
/// it is already the next day in UTC.
/// </summary>
public static class WardCollectionAnalytics
{
    /// <summary>Contracts with at least one instalment past its due date, most overdue first.</summary>
    public static IReadOnlyList<WardDebtorDto> Debtors(
        IReadOnlyList<WardUnpaidItemRow> unpaid,
        DateOnly today,
        IReadOnlyDictionary<long, DateTime> lastReminders)
    {
        var startOfToday = BusinessCalendar.StartOfDayUtc(today);
        return unpaid
            .GroupBy(item => item.ContractId)
            .Select(contract =>
            {
                var overdue = contract.Where(item => item.DueDate < today).ToList();
                if (overdue.Count == 0)
                {
                    return null;
                }

                var first = contract.First();
                var oldest = overdue.Min(item => item.DueDate);
                DateTime? reminded = lastReminders.TryGetValue(contract.Key, out var at)
                    ? DateTime.SpecifyKind(at, DateTimeKind.Utc)
                    : null;
                return new WardDebtorDto(
                    contract.Key,
                    first.VendorName,
                    first.VendorPhone,
                    first.BusinessName,
                    first.SlotCode,
                    first.ZoneName,
                    overdue.Count,
                    overdue.Sum(item => item.Amount),
                    contract.Where(item => item.DueDate >= today).Sum(item => item.Amount),
                    oldest,
                    today.DayNumber - oldest.DayNumber,
                    reminded,
                    reminded >= startOfToday);
            })
            .OfType<WardDebtorDto>()
            .OrderByDescending(debtor => debtor.DaysOverdue)
            .ThenByDescending(debtor => debtor.OverdueAmount)
            .ToList();
    }

    public static bool PaidOnTime(WardFeeItemRow item) =>
        item.ItemStatus == FeeItemStatuses.Paid
        && item.PaidAt is { } paidAt
        && paidAt < BusinessCalendar.StartOfDayUtc(item.DueDate.AddDays(1));

    public static CollectionPerformanceDto Performance(
        DateOnly from,
        DateOnly to,
        IReadOnlyList<WardFeeItemRow> activity,
        IReadOnlyList<WardUnpaidItemRow> unpaid,
        IReadOnlyList<WardZoneRow> zones)
    {
        var fromUtc = BusinessCalendar.StartOfDayUtc(from);
        var toExclusiveUtc = BusinessCalendar.StartOfDayUtc(to.AddDays(1));

        // "Due in the period": current revisions only. A superseded instalment is no longer owed.
        var due = activity.Where(item => item.IsCurrent && item.DueDate >= from && item.DueDate <= to).ToList();
        var paid = due.Where(item => item.ItemStatus == FeeItemStatuses.Paid).ToList();
        var onTime = paid.Count(PaidOnTime);

        // Revenue is never filtered by revision: money received stays revenue.
        var collectedByZone = activity
            .Where(item => item.ItemStatus == FeeItemStatuses.Paid
                && item.PaidAt >= fromUtc && item.PaidAt < toExclusiveUtc)
            .GroupBy(item => item.ZoneId)
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Amount));
        var owedByZone = unpaid
            .GroupBy(item => item.ZoneId)
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Amount));

        return new CollectionPerformanceDto(
            from,
            to,
            due.Sum(item => item.Amount),
            due.Count,
            paid.Sum(item => item.Amount),
            onTime,
            paid.Count - onTime,
            due.Count - paid.Count,
            due.Count == 0 ? null : Math.Round((double)onTime / due.Count, 4),
            zones
                .Select(zone => new ZoneCollectionDto(
                    zone.ZoneId,
                    zone.ZoneName,
                    zone.SlotCount,
                    zone.RentedSlots,
                    collectedByZone.GetValueOrDefault(zone.ZoneId),
                    owedByZone.GetValueOrDefault(zone.ZoneId)))
                .OrderByDescending(zone => zone.Outstanding)
                .ThenBy(zone => zone.ZoneName, StringComparer.Create(
                    System.Globalization.CultureInfo.GetCultureInfo("vi-VN"), ignoreCase: true))
                .ToList());
    }

    /// <summary>The <paramref name="count"/> calendar months ending with <paramref name="today"/>'s, oldest first.</summary>
    public static IReadOnlyList<(int Year, int Month)> MonthsEnding(DateOnly today, int count)
    {
        var first = new DateOnly(today.Year, today.Month, 1).AddMonths(-(count - 1));
        return Enumerable.Range(0, count)
            .Select(offset => first.AddMonths(offset))
            .Select(month => (month.Year, month.Month))
            .ToList();
    }

    public static IReadOnlyList<MonthlyCollectionDto> Trend(
        IReadOnlyList<(int Year, int Month)> months,
        IReadOnlyList<WardFeeItemRow> activity,
        IReadOnlyList<WardPenaltyPaymentRow> penalties)
    {
        static (int, int) MonthOf(DateOnly day) => (day.Year, day.Month);

        var feeCollected = activity
            .Where(item => item.ItemStatus == FeeItemStatuses.Paid && item.PaidAt is not null)
            .ToLookup(item => MonthOf(BusinessCalendar.DateOf(item.PaidAt!.Value)));
        var feeDue = activity.Where(item => item.IsCurrent).ToLookup(item => MonthOf(item.DueDate));
        var penaltyCollected = penalties.ToLookup(penalty => MonthOf(BusinessCalendar.DateOf(penalty.PaidAt)));

        return months
            .Select(month => new MonthlyCollectionDto(
                month.Year,
                month.Month,
                feeCollected[month].Sum(item => item.Amount),
                penaltyCollected[month].Sum(penalty => penalty.Amount),
                feeDue[month].Sum(item => item.Amount),
                feeDue[month].Where(PaidOnTime).Sum(item => item.Amount)))
            .ToList();
    }
}
