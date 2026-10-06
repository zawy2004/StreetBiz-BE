using System.Globalization;
using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.DTOs.Finance;
using StreetBiz.Application.Features.WardSlots;

namespace StreetBiz.Application.Features.Finance.WardReports;

/// <summary>WARD-14: households with overdue rental fees, most overdue first.</summary>
public sealed record ListWardDebtorsQuery : IRequest<IReadOnlyList<WardDebtorDto>>;

public sealed class ListWardDebtorsQueryHandler(
    IWardActorContext wardActorContext,
    IWardCollectionRepository collections,
    TimeProvider clock) : IRequestHandler<ListWardDebtorsQuery, IReadOnlyList<WardDebtorDto>>
{
    public async Task<IReadOnlyList<WardDebtorDto>> Handle(ListWardDebtorsQuery request, CancellationToken cancellationToken)
    {
        var actor = await wardActorContext.RequireAsync(cancellationToken);
        var unpaid = await collections.ListUnpaidFeeItemsAsync(actor.WardId, cancellationToken);
        var reminders = await collections.GetLastDebtRemindersAsync(
            unpaid.Select(item => item.ContractId).Distinct().ToArray(), cancellationToken);
        return WardCollectionAnalytics.Debtors(unpaid, BusinessCalendar.Today(clock), reminders);
    }
}

/// <summary>WARD-14: on-time rate for fees falling due in a period, and collections by zone.</summary>
public sealed record GetCollectionPerformanceQuery(DateOnly? From, DateOnly? To) : IRequest<CollectionPerformanceDto>;

public sealed class GetCollectionPerformanceQueryValidator : AbstractValidator<GetCollectionPerformanceQuery>
{
    public GetCollectionPerformanceQueryValidator() =>
        RuleFor(x => x)
            .Must(x => x.From is null || x.To is null || x.From <= x.To)
            .WithMessage("From must not be after To.");
}

public sealed class GetCollectionPerformanceQueryHandler(
    IWardActorContext wardActorContext,
    IWardCollectionRepository collections,
    TimeProvider clock) : IRequestHandler<GetCollectionPerformanceQuery, CollectionPerformanceDto>
{
    public async Task<CollectionPerformanceDto> Handle(
        GetCollectionPerformanceQuery request, CancellationToken cancellationToken)
    {
        var actor = await wardActorContext.RequireAsync(cancellationToken);
        var today = BusinessCalendar.Today(clock);
        // Same default period as the collection report: the 1st of this month through today.
        var from = request.From ?? new DateOnly(today.Year, today.Month, 1);
        var to = request.To ?? today;

        var activity = await collections.ListFeeActivityAsync(
            actor.WardId, from, to,
            BusinessCalendar.StartOfDayUtc(from), BusinessCalendar.StartOfDayUtc(to.AddDays(1)),
            cancellationToken);
        var unpaid = await collections.ListUnpaidFeeItemsAsync(actor.WardId, cancellationToken);
        var zones = await collections.ListZonesAsync(actor.WardId, cancellationToken);
        return WardCollectionAnalytics.Performance(from, to, activity, unpaid, zones);
    }
}

/// <summary>WARD-14: collections month by month, for the trend chart.</summary>
public sealed record GetCollectionTrendQuery(int Months = 6) : IRequest<IReadOnlyList<MonthlyCollectionDto>>;

public sealed class GetCollectionTrendQueryValidator : AbstractValidator<GetCollectionTrendQuery>
{
    public GetCollectionTrendQueryValidator() => RuleFor(x => x.Months).InclusiveBetween(1, 12);
}

public sealed class GetCollectionTrendQueryHandler(
    IWardActorContext wardActorContext,
    IWardCollectionRepository collections,
    TimeProvider clock) : IRequestHandler<GetCollectionTrendQuery, IReadOnlyList<MonthlyCollectionDto>>
{
    public async Task<IReadOnlyList<MonthlyCollectionDto>> Handle(
        GetCollectionTrendQuery request, CancellationToken cancellationToken)
    {
        var actor = await wardActorContext.RequireAsync(cancellationToken);
        var today = BusinessCalendar.Today(clock);
        var months = WardCollectionAnalytics.MonthsEnding(today, request.Months);
        var first = new DateOnly(months[0].Year, months[0].Month, 1);
        var last = new DateOnly(today.Year, today.Month, 1).AddMonths(1).AddDays(-1);
        var fromUtc = BusinessCalendar.StartOfDayUtc(first);
        var toExclusiveUtc = BusinessCalendar.StartOfDayUtc(last.AddDays(1));

        var activity = await collections.ListFeeActivityAsync(
            actor.WardId, first, last, fromUtc, toExclusiveUtc, cancellationToken);
        var penalties = await collections.ListPenaltyPaymentsAsync(
            actor.WardId, fromUtc, toExclusiveUtc, cancellationToken);
        return WardCollectionAnalytics.Trend(months, activity, penalties);
    }
}

/// <summary>
/// WARD-14: an officer reminds one household of its overdue fees. One reminder per contract per
/// Vietnamese day: a vendor reminded every few minutes is harassed, not reminded. Only overdue
/// debt can be reminded of; an instalment not yet due is not the ward's business to chase.
/// </summary>
public sealed record RemindDebtorCommand(long ContractId) : IRequest<DebtReminderDto>;

public sealed class RemindDebtorCommandHandler(
    IWardActorContext wardActorContext,
    IWardCollectionRepository collections,
    TimeProvider clock) : IRequestHandler<RemindDebtorCommand, DebtReminderDto>
{
    private static readonly CultureInfo Vietnamese = CultureInfo.GetCultureInfo("vi-VN");

    public async Task<DebtReminderDto> Handle(RemindDebtorCommand request, CancellationToken cancellationToken)
    {
        var actor = await wardActorContext.RequireAsync(cancellationToken);
        var now = clock.GetUtcNow().UtcDateTime;
        var today = BusinessCalendar.Today(clock);

        var owed = (await collections.ListUnpaidFeeItemsAsync(actor.WardId, cancellationToken))
            .Where(item => item.ContractId == request.ContractId)
            .ToList();
        if (owed.Count == 0)
        {
            // Another ward's contract, or nothing owed: both look the same from here.
            throw new NotFoundException(WardCollectionMessages.NoDebtFound);
        }

        var debtor = WardCollectionAnalytics.Debtors(
                owed, today, await collections.GetLastDebtRemindersAsync([request.ContractId], cancellationToken))
            .SingleOrDefault()
            ?? throw new DomainRuleException(WardCollectionMessages.NothingOverdue);
        if (debtor.RemindedToday)
        {
            throw new ConflictException(WardCollectionMessages.AlreadyRemindedToday(
                TimeZoneInfo.ConvertTimeFromUtc(debtor.LastRemindedAt!.Value, BusinessCalendar.TimeZone)));
        }

        var amount = debtor.OverdueAmount.ToString("#,##0", Vietnamese);
        await collections.SendDebtReminderAsync(
            request.ContractId,
            owed[0].VendorUserId,
            actor.UserId,
            "Phường nhắc thanh toán phí thuê ô",
            $"Ô {debtor.SlotCode} đang có {debtor.OverdueCount} kỳ phí quá hạn, tổng {amount} đ "
            + $"(quá hạn {debtor.DaysOverdue} ngày). Vui lòng thanh toán trên StreetBiz để tránh bị "
            + "xử lý theo quy định.",
            now,
            cancellationToken);
        return new DebtReminderDto(request.ContractId, DateTime.SpecifyKind(now, DateTimeKind.Utc));
    }
}

public static class WardCollectionMessages
{
    public const string NoDebtFound = "Không tìm thấy khoản nợ phí của hợp đồng này trong phường.";
    public const string NothingOverdue = "Hợp đồng này chưa có kỳ phí nào quá hạn.";

    public static string AlreadyRemindedToday(DateTime localTime) =>
        $"Đã nhắc hộ này hôm nay lúc {localTime:HH:mm}. Mỗi hộ chỉ được nhắc một lần mỗi ngày.";
}
