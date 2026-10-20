using MediatR;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.DTOs.Finance;
using StreetBiz.Application.Features.WardSlots;

namespace StreetBiz.Application.Features.Finance.WardReports;

/// <summary>WARD-15.</summary>
public sealed record GetWardDashboardQuery : IRequest<WardDashboardDto>;

public sealed class GetWardDashboardQueryHandler(
    IWardActorContext wardActorContext,
    IWardReportRepository reports,
    TimeProvider clock) : IRequestHandler<GetWardDashboardQuery, WardDashboardDto>
{
    public async Task<WardDashboardDto> Handle(GetWardDashboardQuery request, CancellationToken cancellationToken)
    {
        var actor = await wardActorContext.RequireAsync(cancellationToken);
        var dashboard = await reports.GetDashboardAsync(actor.WardId, BusinessCalendar.Today(clock), cancellationToken);
        return dashboard.ToDto();
    }
}
