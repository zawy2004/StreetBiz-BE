using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.PlatformAdministration;

namespace StreetBiz.Application.Features.PlatformAdministration;

/// <summary>ADM-06: the platform's own numbers. Ward revenue (rent, penalties) is deliberately absent.</summary>
public sealed record GetPlatformDashboardQuery(DateOnly? From, DateOnly? To)
    : IRequest<PlatformDashboardDto>;

public static class DashboardPeriods
{
    public const int DefaultDays = 30;
    public const int MaxDays = 366;

    /// <summary>The period shown (Vietnamese calendar days, both ends inclusive) and the one just before it.</summary>
    public static (DateOnly From, DateOnly To, DateOnly PreviousFrom, DateOnly PreviousTo) Resolve(
        DateOnly? from,
        DateOnly? to,
        DateOnly today)
    {
        var end = to ?? today;
        var start = from ?? end.AddDays(-(DefaultDays - 1));
        var days = end.DayNumber - start.DayNumber + 1;
        var previousTo = start.AddDays(-1);
        return (start, end, previousTo.AddDays(-(days - 1)), previousTo);
    }
}

public sealed class GetPlatformDashboardQueryValidator : AbstractValidator<GetPlatformDashboardQuery>
{
    public GetPlatformDashboardQueryValidator()
    {
        RuleFor(x => x)
            .Must(x => x.From is null || x.To is null || x.From <= x.To)
            .WithMessage("The start date must not be after the end date.")
            .Must(x =>
            {
                if (x.From is null || x.To is null)
                {
                    return true;
                }

                return x.To.Value.DayNumber - x.From.Value.DayNumber + 1 <= DashboardPeriods.MaxDays;
            })
            .WithMessage($"The period cannot be longer than {DashboardPeriods.MaxDays} days.");
    }
}

public sealed class GetPlatformDashboardQueryHandler(
    IPlatformAdminContext actorContext,
    IPlatformAdministrationRepository repository,
    IDateTimeProvider clock) : IRequestHandler<GetPlatformDashboardQuery, PlatformDashboardDto>
{
    public async Task<PlatformDashboardDto> Handle(
        GetPlatformDashboardQuery request,
        CancellationToken cancellationToken)
    {
        await actorContext.RequireAsync(cancellationToken);
        var (from, to, previousFrom, previousTo) =
            DashboardPeriods.Resolve(request.From, request.To, BusinessCalendar.Today(clock.UtcNow));
        var facts = await repository.GetDashboardFactsAsync(
            new DashboardRange(from, to, previousFrom, previousTo),
            cancellationToken);
        return PlatformDashboardBuilder.Build(from, to, previousFrom, previousTo, facts);
    }
}

/// <summary>Plain arithmetic over the facts the repository counted, kept apart so it can be tested without a database.</summary>
public static class PlatformDashboardBuilder
{
    public static PlatformDashboardDto Build(
        DateOnly from,
        DateOnly to,
        DateOnly previousFrom,
        DateOnly previousTo,
        PlatformDashboardFacts facts)
    {
        var current = facts.Current;
        var previous = facts.Previous;
        var snapshot = facts.Snapshot;
        return new PlatformDashboardDto(
            from,
            to,
            previousFrom,
            previousTo,
            new DashboardUsersDto(
                Metric(current.NewUsers, previous.NewUsers),
                snapshot.UsersTotal,
                snapshot.Customers,
                snapshot.Vendors,
                snapshot.WardAuthorities,
                snapshot.PlatformAdmins,
                snapshot.SuspendedUsers),
            new DashboardMarketplaceDto(
                snapshot.Vendors,
                snapshot.StorefrontsOpen,
                snapshot.StorefrontsPaused,
                snapshot.StorefrontsClosed,
                snapshot.StorefrontsHidden),
            new DashboardOrdersDto(
                Metric(current.OrdersPlaced, previous.OrdersPlaced),
                Metric(current.OrdersCompleted, previous.OrdersCompleted),
                Metric(current.Gmv, previous.Gmv),
                Metric(
                    Rate(current.OrdersCancelled + current.OrdersRejected, current.OrdersPlaced),
                    Rate(previous.OrdersCancelled + previous.OrdersRejected, previous.OrdersPlaced)),
                facts.OrdersByStatus.Select(row => new DashboardOrderStatusDto(row.Status, row.Count)).ToArray()),
            new DashboardModerationDto(
                snapshot.PendingReports,
                Metric(current.ReportsFiled, previous.ReportsFiled),
                current.AverageReviewHours is { } hours ? Math.Round(hours, 1) : null,
                snapshot.OpenComplaints,
                snapshot.PendingRefunds,
                snapshot.PendingRefundAmount),
            FillDays(from, to, facts.Days),
            facts.TopStorefronts
                .Select(row => new DashboardTopStorefrontDto(
                    row.StorefrontId, row.StorefrontName, row.CompletedOrders, row.Revenue))
                .ToArray());
    }

    /// <summary>The change against the previous period; null (not infinity, not NaN) when that period was zero.</summary>
    public static DashboardMetricDto Metric(decimal value, decimal previous) => new(
        value,
        previous,
        previous == 0m ? null : Math.Round((value - previous) / previous * 100m, 1, MidpointRounding.AwayFromZero));

    /// <summary>A share in percent with one decimal; an empty denominator is 0, never a division by zero.</summary>
    public static decimal Rate(int part, int whole) =>
        whole <= 0 ? 0m : Math.Round(100m * part / whole, 1, MidpointRounding.AwayFromZero);

    /// <summary>One point per calendar day of the period, so a quiet day shows as 0 instead of a gap.</summary>
    public static IReadOnlyList<DashboardDayDto> FillDays(
        DateOnly from,
        DateOnly to,
        IReadOnlyList<DashboardDayRow> rows)
    {
        var byDay = rows.GroupBy(row => row.Day).ToDictionary(
            group => group.Key,
            group => new DashboardDayDto(
                group.Key,
                group.Sum(row => row.OrdersPlaced),
                group.Sum(row => row.OrdersCompleted),
                group.Sum(row => row.Gmv)));
        var days = new List<DashboardDayDto>();
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            days.Add(byDay.GetValueOrDefault(day) ?? new DashboardDayDto(day, 0, 0, 0m));
        }

        return days;
    }
}
