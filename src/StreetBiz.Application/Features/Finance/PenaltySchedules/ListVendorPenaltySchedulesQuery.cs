using MediatR;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Finance;

namespace StreetBiz.Application.Features.Finance.PenaltySchedules;

/// <summary>
/// WARD-03, seen from the vendor: the penalty rates in force today in each ward where the vendor
/// has a registration. A penalty is calculated strictly from this schedule (BR-33), so the vendor
/// can read the price of a violation before it happens, not only on the penalty notice.
/// </summary>
public sealed record ListVendorPenaltySchedulesQuery : IRequest<IReadOnlyList<VendorWardPenaltyScheduleDto>>;

public sealed class ListVendorPenaltySchedulesQueryHandler(
    IVendorContext vendorContext,
    IFinanceRepository finance,
    IDateTimeProvider clock)
    : IRequestHandler<ListVendorPenaltySchedulesQuery, IReadOnlyList<VendorWardPenaltyScheduleDto>>
{
    public async Task<IReadOnlyList<VendorWardPenaltyScheduleDto>> Handle(
        ListVendorPenaltySchedulesQuery request, CancellationToken cancellationToken)
    {
        var vendorId = await vendorContext.RequireVendorIdAsync(cancellationToken);
        var rates = await finance.ListWardPenaltyRatesAsync(vendorId, BusinessCalendar.Today(clock.UtcNow), cancellationToken);

        return rates
            .GroupBy(rate => (rate.WardUnitId, rate.WardName))
            .Select(ward => new VendorWardPenaltyScheduleDto(
                ward.Key.WardUnitId,
                ward.Key.WardName,
                ward.Select(rate => new VendorPenaltyRateDto(
                    rate.ViolationType, rate.ViolationTypeName, rate.PenaltyAmount, rate.LegalBasis)).ToList()))
            .ToList();
    }
}
