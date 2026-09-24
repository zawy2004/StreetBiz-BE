using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.DTOs.Finance;
using StreetBiz.Application.Features.WardSlots;

namespace StreetBiz.Application.Features.Finance.WardReports;

/// <summary>
/// WARD-14. Defaults to the 1st of the current month through today when From/To are omitted —
/// the "Tháng hiện tại" period the ward screen shows by default.
/// </summary>
public sealed record GetCollectionReportQuery(DateOnly? From, DateOnly? To) : IRequest<CollectionReportDto>;

public sealed class GetCollectionReportQueryValidator : AbstractValidator<GetCollectionReportQuery>
{
    public GetCollectionReportQueryValidator() =>
        RuleFor(x => x)
            .Must(x => x.From is null || x.To is null || x.From <= x.To)
            .WithMessage("From must not be after To.");
}

public sealed class GetCollectionReportQueryHandler(
    IWardActorContext wardActorContext,
    IWardReportRepository reports,
    TimeProvider clock) : IRequestHandler<GetCollectionReportQuery, CollectionReportDto>
{
    public async Task<CollectionReportDto> Handle(GetCollectionReportQuery request, CancellationToken cancellationToken)
    {
        var actor = await wardActorContext.RequireAsync(cancellationToken);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var from = request.From ?? new DateOnly(today.Year, today.Month, 1);
        var to = request.To ?? today;

        var report = await reports.GetCollectionReportAsync(actor.WardId, from, to, cancellationToken);
        return report.ToDto(from, to);
    }
}
