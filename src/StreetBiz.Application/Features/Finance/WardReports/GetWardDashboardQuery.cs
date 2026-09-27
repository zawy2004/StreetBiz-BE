using MediatR;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.DTOs.Finance;
using StreetBiz.Application.Features.WardSlots;

namespace StreetBiz.Application.Features.Finance.WardReports;

/// <summary>WARD-15.</summary>
public sealed record GetWardDashboardQuery : IRequest<WardDashboardDto>;

public sealed class GetWardDashboardQueryHandler(
    IWardActorContext wardActorContext,
    IWardReportRepository reports) : IRequestHandler<GetWardDashboardQuery, WardDashboardDto>
{
    public async Task<WardDashboardDto> Handle(GetWardDashboardQuery request, CancellationToken cancellationToken)
    {
        var actor = await wardActorContext.RequireAsync(cancellationToken);
        var dashboard = await reports.GetDashboardAsync(actor.WardId, cancellationToken);
        return dashboard.ToDto();
    }
}
