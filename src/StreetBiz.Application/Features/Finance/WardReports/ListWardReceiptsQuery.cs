using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.DTOs.Finance;
using StreetBiz.Application.Features.WardSlots;

namespace StreetBiz.Application.Features.Finance.WardReports;

/// <summary>
/// "Lịch sử thu": every receipt of the officer's ward in a period, newest first, each with how it
/// was paid and the reference to check it against (the payOS order in payOS's own transaction list,
/// the household's Treasury receipt number, or the officer's note for money taken in person).
/// Defaults to the 1st of the month through today, like the collection report.
/// </summary>
public sealed record ListWardReceiptsQuery(DateOnly? From, DateOnly? To) : IRequest<WardReceiptListDto>;

public static class WardReceiptRules
{
    /// <summary>At most a year per request: a receipt list is read on screen, not exported.</summary>
    public const int MaxDays = 366;

    public const string FromAfterTo = "Ngày bắt đầu phải trước hoặc bằng ngày kết thúc.";
    public const string TooLong = "Chỉ xem được tối đa một năm mỗi lần.";
}

public sealed class ListWardReceiptsQueryValidator : AbstractValidator<ListWardReceiptsQuery>
{
    public ListWardReceiptsQueryValidator()
    {
        RuleFor(x => x)
            .Must(x => x.From is null || x.To is null || x.From <= x.To)
            .WithMessage(WardReceiptRules.FromAfterTo);
        RuleFor(x => x)
            .Must(x => x.From is null || x.To is null || x.To.Value.DayNumber - x.From.Value.DayNumber < WardReceiptRules.MaxDays)
            .WithMessage(WardReceiptRules.TooLong);
    }
}

public sealed class ListWardReceiptsQueryHandler(
    IWardActorContext wardActorContext,
    IWardReportRepository reports,
    TimeProvider clock) : IRequestHandler<ListWardReceiptsQuery, WardReceiptListDto>
{
    public async Task<WardReceiptListDto> Handle(ListWardReceiptsQuery request, CancellationToken cancellationToken)
    {
        var actor = await wardActorContext.RequireAsync(cancellationToken);
        var to = request.To ?? BusinessCalendar.Today(clock);
        var from = request.From ?? new DateOnly(to.Year, to.Month, 1);

        // Only one end given: the other is filled in, so the rules are checked again on the result.
        if (from > to)
        {
            throw new DomainRuleException(WardReceiptRules.FromAfterTo);
        }

        if (to.DayNumber - from.DayNumber >= WardReceiptRules.MaxDays)
        {
            throw new DomainRuleException(WardReceiptRules.TooLong);
        }

        var rows = await reports.ListWardReceiptsAsync(actor.WardId, from, to, cancellationToken);
        return new WardReceiptListDto(
            from,
            to,
            [.. rows.Select(row => new WardReceiptDto(
                row.InvoiceId,
                row.InvoiceNumber,
                row.IssuedAt,
                row.Kind,
                row.PayerName,
                row.PayerPhone,
                row.SlotCode,
                row.Description,
                row.Amount,
                row.PaymentProvider,
                row.ProviderReference,
                row.PaymentStatus))]);
    }
}
