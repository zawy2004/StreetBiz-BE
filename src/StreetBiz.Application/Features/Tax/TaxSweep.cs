using System.Globalization;
using MediatR;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;

namespace StreetBiz.Application.Features.Tax;

/// <summary>What one run of the daily tax sweep did.</summary>
public sealed record TaxSweepResult(
    int ReferralsCreated,
    int ReferralsClosed,
    int ThresholdReminders,
    int DeadlineReminders);

/// <summary>
/// The daily tax sweep, run by the background service: refresh every ward's list of cases to pass
/// to the tax office, tell households whose revenue is near, over or forecast to cross the no-tax
/// threshold, and remind them of notices coming due or overdue. Every notification is sent once
/// (the repository's NotifyOnce keys on a record), so running it again the same day sends nothing new.
/// </summary>
public sealed record RunTaxSweepCommand : IRequest<TaxSweepResult>;

public sealed class RunTaxSweepCommandHandler(
    ITaxRepository repository,
    TimeProvider clock) : IRequestHandler<RunTaxSweepCommand, TaxSweepResult>
{
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");

    /// <summary>A notice overdue by more than this is left alone: the household has been told.</summary>
    private const int OverdueReminderDays = 30;

    private const string NotificationType = "TAX";

    public async Task<TaxSweepResult> Handle(RunTaxSweepCommand request, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var today = BusinessCalendar.Today(clock);

        var referrals = await repository.RefreshReferralsAsync(
            null, now, TaxReferralWindow.LookbackDays, cancellationToken);

        var vendors = await repository.ListTaxableVendorsAsync(cancellationToken);
        if (vendors.Count == 0)
        {
            return new TaxSweepResult(referrals.Created, referrals.AutoClosed, 0, 0);
        }

        var thresholdReminders = await RemindAboutThresholdAsync(vendors, today, now, cancellationToken);
        var deadlineReminders = 0;
        foreach (var taxYear in new[] { today.Year - 1, today.Year }.Where(year => year >= 2026))
        {
            deadlineReminders += await RemindAboutDeadlinesAsync(vendors, taxYear, today, now, cancellationToken);
        }

        return new TaxSweepResult(referrals.Created, referrals.AutoClosed, thresholdReminders, deadlineReminders);
    }

    private async Task<int> RemindAboutThresholdAsync(
        IReadOnlyList<(long VendorId, long UserId)> vendors, DateOnly today, DateTime now, CancellationToken cancellationToken)
    {
        var policy = await repository.GetPolicyAsync(today.Year, cancellationToken);
        if (policy is null)
        {
            return 0;
        }

        var userOf = vendors.ToDictionary(vendor => vendor.VendorId, vendor => vendor.UserId);
        var revenue = await repository.GetYearRevenueAsync([.. userOf.Keys], today.Year, today, cancellationToken);
        var sent = 0;
        foreach (var row in revenue.Where(row => row.Total > 0))
        {
            var level = ThresholdLevels.Of(row.Total, policy);
            var projection = RevenueProjections.Project(row.Total, row.FirstDate, today, today.Year, policy.RevenueThreshold);
            var threshold = TaxFormat.Money(policy.RevenueThreshold);
            var userId = userOf[row.VendorId];

            if (level == ThresholdLevels.Over)
            {
                sent += await Send(userId, $"Doanh thu năm {today.Year} đã vượt ngưỡng không chịu thuế",
                    $"Doanh thu ghi nhận {Money(row.Total)}, vượt ngưỡng {threshold}. Bạn cần dùng hoá đơn điện tử, khai thuế " +
                    "theo quý và nộp thuế theo quy định. Mở mục Thuế để xem việc phải làm.",
                    "TAX_THRESHOLD", today.Year * 10L + 2, now, cancellationToken);
            }
            else if (level == ThresholdLevels.Near)
            {
                var percent = Math.Round(row.Total / policy.RevenueThreshold * 100m, 0);
                sent += await Send(userId, $"Doanh thu năm {today.Year} đã gần ngưỡng không chịu thuế",
                    $"Doanh thu ghi nhận {Money(row.Total)}, đạt {percent.ToString("0", Vi)}% ngưỡng {threshold}. " +
                    "Khi vượt ngưỡng bạn phải nộp thuế và dùng hoá đơn điện tử. Mở mục Thuế để xem dự báo.",
                    "TAX_THRESHOLD", today.Year * 10L + 1, now, cancellationToken);
            }
            else if (projection.WillCross && projection.CrossingDate is { } crossing)
            {
                sent += await Send(userId, $"Doanh thu năm {today.Year} dự kiến vượt ngưỡng không chịu thuế",
                    $"Với tốc độ bán hiện tại, doanh thu dự kiến vượt ngưỡng {threshold} vào khoảng " +
                    $"{TaxFormat.Day(crossing)}. Mở mục Thuế để chuẩn bị trước.",
                    "TAX_THRESHOLD", today.Year * 10L + 3, now, cancellationToken);
            }
        }

        return sent;
    }

    private async Task<int> RemindAboutDeadlinesAsync(
        IReadOnlyList<(long VendorId, long UserId)> vendors,
        int taxYear,
        DateOnly today,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var policy = await repository.GetPolicyAsync(taxYear, cancellationToken);
        if (policy is null)
        {
            return 0;
        }

        var userOf = vendors.ToDictionary(vendor => vendor.VendorId, vendor => vendor.UserId);
        var vendorIds = userOf.Keys.ToList();
        var sent = 0;

        foreach (var period in TaxCalendar.Periods(taxYear, policy))
        {
            // Nothing to file until the period is over, and nothing to chase long after its due date.
            var daysLeft = period.Due.DayNumber - today.DayNumber;
            if (today <= period.End || daysLeft > TaxCalendar.DueSoonDays || daysLeft < -OverdueReminderDays)
            {
                continue;
            }

            var stage = daysLeft < 0 ? 3 : daysLeft <= 3 ? 2 : 1;
            var filed = await repository.ListFiledVendorsAsync(vendorIds, taxYear, period.Type, period.No, cancellationToken);
            var due = vendorIds.Where(vendorId => !filed.Contains(vendorId)).ToList();

            if (period.Type == TaxPeriodTypes.Quarter)
            {
                // A quarter is only a declaration for a household above the threshold by its end.
                var cumulative = await repository.GetYearRevenueAsync(due, taxYear, period.End, cancellationToken);
                due = [.. cumulative.Where(row => row.Total > policy.RevenueThreshold).Select(row => row.VendorId)];
            }

            var relatedId = taxYear * 1000L + period.No * 10 + stage;
            var (title, body) = stage switch
            {
                3 => ($"Quá hạn: {period.Label}",
                    $"Hạn là {TaxFormat.Day(period.Due)}. Mở mục Thuế để xem số liệu tổng hợp và xác nhận khi bạn đã nộp."),
                2 => ($"Còn {daysLeft} ngày: {period.Label}",
                    $"Hạn là {TaxFormat.Day(period.Due)}. Mở mục Thuế để xem số liệu tổng hợp và xác nhận khi bạn đã nộp."),
                _ => ($"Sắp đến hạn: {period.Label}",
                    $"Hạn là {TaxFormat.Day(period.Due)}, còn {daysLeft} ngày. Mở mục Thuế để xem số liệu tổng hợp."),
            };

            foreach (var vendorId in due)
            {
                sent += await Send(userOf[vendorId], title, body, "TAX_DEADLINE", relatedId, now, cancellationToken);
            }
        }

        return sent;
    }

    private async Task<int> Send(
        long userId, string title, string body, string relatedType, long relatedId, DateTime now, CancellationToken cancellationToken) =>
        await repository.NotifyOnceAsync(userId, NotificationType, title, body, relatedType, relatedId, now, cancellationToken)
            ? 1
            : 0;

    private static string Money(decimal value) => $"{value.ToString("N0", Vi)} đ";
}
