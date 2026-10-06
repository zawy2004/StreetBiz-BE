using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Features.WardSlots;

namespace StreetBiz.Application.Features.Finance.WardReports;

/// <summary>
/// WARD-14 as an .xlsx workbook: the same totals as the screen for the same period, plus the
/// receipts behind them. Defaults to the 1st of the current month through today, like the screen.
/// </summary>
public sealed record ExportCollectionReportQuery(DateOnly? From, DateOnly? To) : IRequest<FinanceFile>;

public sealed class ExportCollectionReportQueryValidator : AbstractValidator<ExportCollectionReportQuery>
{
    public ExportCollectionReportQueryValidator() =>
        RuleFor(x => x)
            .Must(x => x.From is null || x.To is null || x.From <= x.To)
            .WithMessage("From must not be after To.");
}

public sealed class ExportCollectionReportQueryHandler(
    IWardActorContext wardActorContext,
    IWardReportRepository reports,
    IWardCollectionRepository collections,
    IFinanceDocumentRenderer renderer,
    TimeProvider clock) : IRequestHandler<ExportCollectionReportQuery, FinanceFile>
{
    public const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public async Task<FinanceFile> Handle(ExportCollectionReportQuery request, CancellationToken cancellationToken)
    {
        var actor = await wardActorContext.RequireAsync(cancellationToken);
        var today = BusinessCalendar.Today(clock);
        var from = request.From ?? new DateOnly(today.Year, today.Month, 1);
        var to = request.To ?? today;

        var report = await reports.GetCollectionReportAsync(actor.WardId, from, to, cancellationToken);
        var invoices = await reports.ListWardInvoicesAsync(actor.WardId, from, to, cancellationToken);
        var wardName = await reports.GetWardNameAsync(actor.WardId, cancellationToken) ?? $"Phường {actor.WardId}";

        // The same figures the screen shows: debtors as of now, on-time rate and zones for the period.
        var unpaid = await collections.ListUnpaidFeeItemsAsync(actor.WardId, cancellationToken);
        var reminders = await collections.GetLastDebtRemindersAsync(
            unpaid.Select(item => item.ContractId).Distinct().ToArray(), cancellationToken);
        var activity = await collections.ListFeeActivityAsync(
            actor.WardId, from, to,
            BusinessCalendar.StartOfDayUtc(from), BusinessCalendar.StartOfDayUtc(to.AddDays(1)),
            cancellationToken);
        var zones = await collections.ListZonesAsync(actor.WardId, cancellationToken);

        var workbook = renderer.RenderCollectionReportXlsx(
            new CollectionReportExport(wardName, from, to, report, invoices, clock.GetUtcNow().UtcDateTime)
            {
                Debtors = WardCollectionAnalytics.Debtors(unpaid, today, reminders),
                Performance = WardCollectionAnalytics.Performance(from, to, activity, unpaid, zones),
            });
        return new FinanceFile($"bao-cao-thu-phi_{from:yyyyMMdd}-{to:yyyyMMdd}.xlsx", XlsxContentType, workbook);
    }
}
