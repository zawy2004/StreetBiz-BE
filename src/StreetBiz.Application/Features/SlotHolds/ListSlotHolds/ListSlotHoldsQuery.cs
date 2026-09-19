using MediatR;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Sidewalk;

namespace StreetBiz.Application.Features.SlotHolds.ListSlotHolds;

/// <summary>The registration's live holds, soonest-expiring first.</summary>
public sealed record ListSlotHoldsQuery(long RegistrationId) : IRequest<IReadOnlyList<SlotHoldDto>>;

public sealed class ListSlotHoldsQueryHandler(IVendorContext vendorContext, ISlotHoldRepository holds, IDateTimeProvider clock)
    : IRequestHandler<ListSlotHoldsQuery, IReadOnlyList<SlotHoldDto>>
{
    public async Task<IReadOnlyList<SlotHoldDto>> Handle(ListSlotHoldsQuery request, CancellationToken cancellationToken)
    {
        await vendorContext.RequireOwnedRegistrationAsync(request.RegistrationId, cancellationToken);

        var rows = await holds.ListActiveByRegistrationAsync(request.RegistrationId, clock.UtcNow, cancellationToken);
        return rows.Select(r => r.ToDto()).ToList();
    }
}
