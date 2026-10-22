using Microsoft.EntityFrameworkCore;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;

namespace StreetBiz.Infrastructure.Persistence.Repositories;

/// <summary>ADM-06: the platform dashboard. Read-only, no tracking, one grouped query per question.</summary>
public sealed partial class PlatformAdministrationRepository
{
    private const int TopStorefrontCount = 5;
    private const int VietnamOffsetHours = 7;

    public async Task<PlatformDashboardFacts> GetDashboardFactsAsync(
        DashboardRange range,
        CancellationToken cancellationToken)
    {
        var from = BusinessCalendar.StartOfDayUtc(range.From);
        var to = BusinessCalendar.EndOfDayUtc(range.To);
        var previousFrom = BusinessCalendar.StartOfDayUtc(range.PreviousFrom);
        var previousTo = BusinessCalendar.EndOfDayUtc(range.PreviousTo);

        var (current, statuses) = await PeriodAsync(from, to, cancellationToken);
        var (previous, _) = await PeriodAsync(previousFrom, previousTo, cancellationToken);
        return new PlatformDashboardFacts(
            current,
            previous,
            await SnapshotAsync(cancellationToken),
            statuses,
            await DaysAsync(from, to, cancellationToken),
            await TopStorefrontsAsync(from, to, cancellationToken));
    }

    private async Task<(DashboardPeriodRow Row, IReadOnlyList<DashboardOrderStatusRow> Statuses)> PeriodAsync(
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken)
    {
        var newUsers = await db.UserAccounts.AsNoTracking()
            .CountAsync(user => user.created_at >= from && user.created_at < to, cancellationToken);

        var statuses = await db.Orders.AsNoTracking()
            .Where(order => order.created_at >= from && order.created_at < to)
            .GroupBy(order => order.order_status)
            .Select(group => new DashboardOrderStatusRow(group.Key, group.Count()))
            .ToListAsync(cancellationToken);

        var completed = await db.Orders.AsNoTracking()
            .Where(order => order.order_status == OrderStatuses.Completed
                && order.completed_at >= from && order.completed_at < to)
            .GroupBy(_ => 1)
            .Select(group => new { Count = group.Count(), Gmv = group.Sum(order => order.total_amount) })
            .SingleOrDefaultAsync(cancellationToken);

        var reportsFiled = await db.ReportedContents.AsNoTracking()
            .CountAsync(report => report.created_at >= from && report.created_at < to, cancellationToken);
        var reviewed = await db.ReportedContents.AsNoTracking()
            .Where(report => report.reviewed_at >= from && report.reviewed_at < to)
            .Select(report => new { report.created_at, report.reviewed_at })
            .ToListAsync(cancellationToken);

        return (
            new DashboardPeriodRow(
                newUsers,
                statuses.Sum(row => row.Count),
                completed?.Count ?? 0,
                CountOf(statuses, OrderStatuses.Cancelled),
                CountOf(statuses, OrderStatuses.Rejected),
                completed?.Gmv ?? 0m,
                reportsFiled,
                reviewed.Count,
                reviewed.Count == 0
                    ? null
                    : reviewed.Average(row => (row.reviewed_at!.Value - row.created_at).TotalHours)),
            statuses.OrderByDescending(row => row.Count).ThenBy(row => row.Status).ToArray());
    }

    private static int CountOf(IEnumerable<DashboardOrderStatusRow> rows, string status) =>
        rows.Where(row => row.Status == status).Sum(row => row.Count);

    private async Task<DashboardSnapshotRow> SnapshotAsync(CancellationToken cancellationToken)
    {
        var roles = await db.UserAccounts.AsNoTracking()
            .GroupBy(user => user.role_code)
            .Select(group => new { Role = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        var suspended = await db.UserAccounts.AsNoTracking()
            .CountAsync(user => user.account_status == AccountStatuses.Suspended, cancellationToken);
        var storefronts = await db.Storefronts.AsNoTracking()
            .GroupBy(storefront => storefront.availability_status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        var hidden = await db.ReportedContents.AsNoTracking()
            .Where(report => report.content_type == ReportedContentTypes.Storefront
                && report.status == ReportedContentStatuses.Hidden)
            .Select(report => report.content_id)
            .Distinct()
            .CountAsync(cancellationToken);
        var pendingReports = await db.ReportedContents.AsNoTracking()
            .CountAsync(report => report.status == ReportedContentStatuses.Pending, cancellationToken);
        var openComplaints = await db.Complaints.AsNoTracking()
            .CountAsync(complaint => complaint.status == ComplaintStatuses.Open
                || complaint.status == ComplaintStatuses.UnderReview, cancellationToken);
        var refunds = await db.RefundTransactions.AsNoTracking()
            .Where(refund => refund.refund_status == RefundPending)
            .GroupBy(_ => 1)
            .Select(group => new { Count = group.Count(), Amount = group.Sum(refund => refund.amount) })
            .SingleOrDefaultAsync(cancellationToken);

        int Role(string code) => roles.Where(row => row.Role == code).Sum(row => row.Count);
        int Storefront(string status) => storefronts.Where(row => row.Status == status).Sum(row => row.Count);
        return new DashboardSnapshotRow(
            roles.Sum(row => row.Count),
            Role(RoleCodes.Customer),
            Role(RoleCodes.Vendor),
            Role(RoleCodes.WardAuthority),
            Role(RoleCodes.PlatformAdmin),
            suspended,
            Storefront("OPEN"),
            Storefront(StorefrontPaused),
            Storefront("CLOSED"),
            hidden,
            pendingReports,
            openComplaints,
            refunds?.Count ?? 0,
            refunds?.Amount ?? 0m);
    }

    /// <summary>Orders placed per Vietnamese calendar day, and orders completed (with their value) per day.</summary>
    private async Task<IReadOnlyList<DashboardDayRow>> DaysAsync(
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken)
    {
        var placed = await db.Orders.AsNoTracking()
            .Where(order => order.created_at >= from && order.created_at < to)
            .GroupBy(order => new
            {
                Year = order.created_at.AddHours(VietnamOffsetHours).Year,
                Month = order.created_at.AddHours(VietnamOffsetHours).Month,
                Day = order.created_at.AddHours(VietnamOffsetHours).Day,
            })
            .Select(group => new { group.Key.Year, group.Key.Month, group.Key.Day, Count = group.Count() })
            .ToListAsync(cancellationToken);
        var completed = await db.Orders.AsNoTracking()
            .Where(order => order.order_status == OrderStatuses.Completed
                && order.completed_at >= from && order.completed_at < to)
            .GroupBy(order => new
            {
                Year = order.completed_at!.Value.AddHours(VietnamOffsetHours).Year,
                Month = order.completed_at!.Value.AddHours(VietnamOffsetHours).Month,
                Day = order.completed_at!.Value.AddHours(VietnamOffsetHours).Day,
            })
            .Select(group => new
            {
                group.Key.Year,
                group.Key.Month,
                group.Key.Day,
                Count = group.Count(),
                Gmv = group.Sum(order => order.total_amount),
            })
            .ToListAsync(cancellationToken);

        return placed
            .Select(row => new DashboardDayRow(new DateOnly(row.Year, row.Month, row.Day), row.Count, 0, 0m))
            .Concat(completed.Select(row => new DashboardDayRow(
                new DateOnly(row.Year, row.Month, row.Day), 0, row.Count, row.Gmv)))
            .ToArray();
    }

    private async Task<IReadOnlyList<DashboardTopStorefrontRow>> TopStorefrontsAsync(
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken)
    {
        // Grouped by id alone and ranked here: the number of storefronts that sold something in a
        // period is small, and it keeps the ORDER BY off an aggregated decimal (not portable).
        var sales = await db.Orders.AsNoTracking()
            .Where(order => order.order_status == OrderStatuses.Completed
                && order.completed_at >= from && order.completed_at < to)
            .GroupBy(order => order.storefront_id)
            .Select(group => new { StorefrontId = group.Key, Count = group.Count(), Revenue = group.Sum(order => order.total_amount) })
            .ToListAsync(cancellationToken);
        var top = sales
            .OrderByDescending(row => row.Revenue)
            .ThenBy(row => row.StorefrontId)
            .Take(TopStorefrontCount)
            .ToArray();
        var ids = top.Select(row => row.StorefrontId).ToArray();
        var names = await db.Storefronts.AsNoTracking()
            .Where(storefront => ids.Contains(storefront.storefront_id))
            .ToDictionaryAsync(storefront => storefront.storefront_id, storefront => storefront.storefront_name, cancellationToken);
        return top
            .Select(row => new DashboardTopStorefrontRow(
                row.StorefrontId, names.GetValueOrDefault(row.StorefrontId) ?? $"#{row.StorefrontId}", row.Count, row.Revenue))
            .ToArray();
    }
}
