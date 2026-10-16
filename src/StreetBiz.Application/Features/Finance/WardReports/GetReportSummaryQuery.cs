using System.Globalization;
using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Features.WardSlots;

namespace StreetBiz.Application.Features.Finance.WardReports;

public static class SummaryTones
{
    public const string Positive = "POSITIVE";
    public const string Neutral = "NEUTRAL";
    public const string Attention = "ATTENTION";
}

public static class SummaryTopics
{
    public const string Collection = "COLLECTION";
    public const string Timeliness = "TIMELINESS";
    public const string Debt = "DEBT";
    public const string Violations = "VIOLATIONS";
    public const string Occupancy = "OCCUPANCY";
    public const string Workload = "WORKLOAD";
}

public sealed record ReportSummaryPointDto(string Tone, string Topic, string Text);

/// <summary>AIC-08: a few sentences the ward officer reads instead of the raw WARD-14/15 tables.</summary>
public sealed record ReportSummaryDto(
    DateOnly From,
    DateOnly To,
    string Headline,
    IReadOnlyList<ReportSummaryPointDto> Points,
    string Basis);

/// <summary>
/// AIC-08. Every figure in the sentences is copied from the report rows, never produced by a
/// model, so the summary cannot say something the tables do not (BR-41: it informs the officer,
/// it decides nothing). The wording rules are fixed thresholds, kept here so they can be read and
/// tested in one place.
/// </summary>
public static class ReportSummaryBuilder
{
    public const string Basis =
        "Tổng hợp tự động từ số liệu của phường trong kỳ đã chọn; chỉ để tham khảo, số liệu gốc ở các bảng bên dưới.";

    private const decimal TrendThresholdPercent = 5m;
    private const int OnTimeGoodPercent = 90;
    private const int OnTimeWeakPercent = 70;
    private const int OccupancyGoodPercent = 80;
    private const int OccupancyLowPercent = 40;
    private const int LargeQueue = 5;

    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");

    public static ReportSummaryDto Build(
        DateOnly from,
        DateOnly to,
        CollectionReportRow current,
        CollectionReportRow previous,
        WardDashboardRow dashboard,
        ReportPeriodFacts facts)
    {
        var points = new List<ReportSummaryPointDto>();
        var collected = current.FeeCollected + current.PenaltyCollected;

        points.Add(CollectionPoint(current, previous));
        if (facts.FeeItemsPaid > 0)
        {
            points.Add(TimelinessPoint(facts));
        }

        points.AddRange(DebtPoints(current, facts));
        points.Add(ViolationPoint(facts));
        if (dashboard.SlotTotal > 0)
        {
            points.Add(OccupancyPoint(dashboard));
        }

        points.Add(WorkloadPoint(dashboard));

        var headline =
            $"{Day(from)} – {Day(to)}: thu {Money(collected)}, nợ quá hạn {Money(current.FeeOverdue)}, " +
            $"{facts.ViolationCount} vi phạm.";
        return new ReportSummaryDto(from, to, headline, points, Basis);
    }

    private static ReportSummaryPointDto CollectionPoint(CollectionReportRow current, CollectionReportRow previous)
    {
        var collected = current.FeeCollected + current.PenaltyCollected;
        var before = previous.FeeCollected + previous.PenaltyCollected;
        var split = $"(tiền thuê {Money(current.FeeCollected)}, tiền phạt {Money(current.PenaltyCollected)})";

        if (collected == 0m && before == 0m)
        {
            return Point(SummaryTones.Neutral, SummaryTopics.Collection,
                "Chưa ghi nhận khoản thu nào trong kỳ này và kỳ trước.");
        }

        if (collected == 0m)
        {
            return Point(SummaryTones.Attention, SummaryTopics.Collection,
                $"Chưa thu được khoản nào trong kỳ này; kỳ trước thu {Money(before)}.");
        }

        if (before == 0m)
        {
            return Point(SummaryTones.Neutral, SummaryTopics.Collection,
                $"Kỳ này thu {Money(collected)} {split}; kỳ trước chưa có khoản thu.");
        }

        var percent = Math.Round((collected - before) / before * 100m, 0, MidpointRounding.AwayFromZero);
        if (percent >= TrendThresholdPercent)
        {
            return Point(SummaryTones.Positive, SummaryTopics.Collection,
                $"Kỳ này thu {Money(collected)} {split}, tăng {percent}% so với kỳ trước ({Money(before)}).");
        }

        if (percent <= -TrendThresholdPercent)
        {
            return Point(SummaryTones.Attention, SummaryTopics.Collection,
                $"Kỳ này thu {Money(collected)} {split}, giảm {Math.Abs(percent)}% so với kỳ trước ({Money(before)}).");
        }

        return Point(SummaryTones.Neutral, SummaryTopics.Collection,
            $"Kỳ này thu {Money(collected)} {split}, gần như không đổi so với kỳ trước ({Money(before)}).");
    }

    private static ReportSummaryPointDto TimelinessPoint(ReportPeriodFacts facts)
    {
        var percent = (int)Math.Round(100m * facts.FeeItemsPaidOnTime / facts.FeeItemsPaid, 0, MidpointRounding.AwayFromZero);
        var tone = percent >= OnTimeGoodPercent ? SummaryTones.Positive
            : percent >= OnTimeWeakPercent ? SummaryTones.Neutral
            : SummaryTones.Attention;
        return Point(tone, SummaryTopics.Timeliness,
            $"{facts.FeeItemsPaidOnTime}/{facts.FeeItemsPaid} kỳ phí nộp trong kỳ này là đúng hạn ({percent}%).");
    }

    private static IEnumerable<ReportSummaryPointDto> DebtPoints(CollectionReportRow current, ReportPeriodFacts facts)
    {
        if (facts.OverdueItemCount > 0)
        {
            var text =
                $"Hiện có {facts.OverdueItemCount} kỳ phí quá hạn của {facts.OverdueVendorCount} hộ, " +
                $"tổng {Money(current.FeeOverdue)}.";
            if (facts.LargestDebtor is { } debtor)
            {
                text += $" Hộ nợ nhiều nhất: {debtor.VendorName} ({Money(debtor.Amount)}).";
            }

            yield return Point(SummaryTones.Attention, SummaryTopics.Debt, text);
        }
        else
        {
            yield return Point(SummaryTones.Positive, SummaryTopics.Debt, "Không có kỳ phí nào đang quá hạn.");
        }

        if (current.PenaltyPending > 0m)
        {
            yield return Point(SummaryTones.Attention, SummaryTopics.Debt,
                $"Còn {Money(current.PenaltyPending)} tiền phạt chưa nộp.");
        }

        if (current.FeePending > 0m)
        {
            yield return Point(SummaryTones.Neutral, SummaryTopics.Debt,
                $"Còn {Money(current.FeePending)} tiền thuê chưa đến hạn thu.");
        }
    }

    private static ReportSummaryPointDto ViolationPoint(ReportPeriodFacts facts)
    {
        if (facts.ViolationCount == 0)
        {
            return Point(SummaryTones.Positive, SummaryTopics.Violations, "Không ghi nhận vi phạm nào trong kỳ.");
        }

        var common = string.Join(", ", facts.TopViolationTypes.Select(t => $"{t.Label} ({t.Count})"));
        var withoutPenalty = facts.ViolationCount - facts.PenaltiesIssued;
        var text =
            $"Ghi nhận {facts.ViolationCount} vi phạm trong kỳ, phổ biến nhất: {common}. " +
            $"Đã ra quyết định phạt {facts.PenaltiesIssued} vụ ({Money(facts.PenaltiesIssuedAmount)})";
        text += withoutPenalty > 0
            ? $"; {withoutPenalty} vụ chưa có khoản phạt hiệu lực."
            : ".";
        return Point(withoutPenalty > 0 ? SummaryTones.Attention : SummaryTones.Neutral, SummaryTopics.Violations, text);
    }

    private static ReportSummaryPointDto OccupancyPoint(WardDashboardRow dashboard)
    {
        var percent = (int)Math.Round(100m * dashboard.SlotRented / dashboard.SlotTotal, 0, MidpointRounding.AwayFromZero);
        var tone = percent >= OccupancyGoodPercent ? SummaryTones.Positive
            : percent < OccupancyLowPercent ? SummaryTones.Attention
            : SummaryTones.Neutral;
        return Point(tone, SummaryTopics.Occupancy,
            $"{dashboard.SlotRented}/{dashboard.SlotTotal} ô đang cho thuê ({percent}%).");
    }

    private static ReportSummaryPointDto WorkloadPoint(WardDashboardRow dashboard)
    {
        var waiting = dashboard.PendingRegistrations + dashboard.PendingApplications;
        if (waiting == 0)
        {
            return Point(SummaryTones.Positive, SummaryTopics.Workload,
                "Không có hồ sơ đăng ký hay đơn thuê ô nào đang chờ xử lý.");
        }

        return Point(
            waiting >= LargeQueue ? SummaryTones.Attention : SummaryTones.Neutral,
            SummaryTopics.Workload,
            $"Đang chờ xử lý: {dashboard.PendingRegistrations} hồ sơ đăng ký, {dashboard.PendingApplications} đơn thuê ô.");
    }

    private static ReportSummaryPointDto Point(string tone, string topic, string text) => new(tone, topic, text);

    private static string Money(decimal amount) => amount.ToString("N0", Vi) + " đ";

    private static string Day(DateOnly day) => day.ToString("dd/MM/yyyy", Vi);
}

/// <summary>
/// AIC-08. Same default period as WARD-14 (the 1st of this month through today). The comparison
/// period is the equally long stretch that ends the day before the selected one starts.
/// </summary>
public sealed record GetReportSummaryQuery(DateOnly? From, DateOnly? To) : IRequest<ReportSummaryDto>;

public sealed class GetReportSummaryQueryValidator : AbstractValidator<GetReportSummaryQuery>
{
    public const int MaxDays = 366;

    public GetReportSummaryQueryValidator()
    {
        RuleFor(x => x)
            .Must(x => x.From is null || x.To is null || x.From <= x.To)
            .WithMessage("From must not be after To.");
        RuleFor(x => x)
            .Must(x => x.From is null || x.To is null || x.To.Value.DayNumber - x.From.Value.DayNumber < MaxDays)
            .WithMessage($"A summary covers at most {MaxDays} days.");
    }
}

public sealed class GetReportSummaryQueryHandler(
    IWardActorContext wardActorContext,
    IWardReportRepository reports,
    TimeProvider clock) : IRequestHandler<GetReportSummaryQuery, ReportSummaryDto>
{
    public async Task<ReportSummaryDto> Handle(GetReportSummaryQuery request, CancellationToken cancellationToken)
    {
        var actor = await wardActorContext.RequireAsync(cancellationToken);
        var today = BusinessCalendar.Today(clock);
        var from = request.From ?? new DateOnly(today.Year, today.Month, 1);
        var to = request.To ?? today;
        if (from > to)
        {
            (from, to) = (to, from);
        }

        var days = to.DayNumber - from.DayNumber + 1;
        var previousTo = from.AddDays(-1);
        var previousFrom = previousTo.AddDays(-(days - 1));

        // One DbContext behind all of these, so they run one after the other.
        var current = await reports.GetCollectionReportAsync(actor.WardId, from, to, cancellationToken);
        var previous = await reports.GetCollectionReportAsync(actor.WardId, previousFrom, previousTo, cancellationToken);
        var dashboard = await reports.GetDashboardAsync(actor.WardId, today, cancellationToken);
        var facts = await reports.GetPeriodFactsAsync(actor.WardId, from, to, cancellationToken);

        return ReportSummaryBuilder.Build(from, to, current, previous, dashboard, facts);
    }
}
