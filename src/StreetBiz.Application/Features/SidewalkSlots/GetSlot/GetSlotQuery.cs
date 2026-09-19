using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Sidewalk;

namespace StreetBiz.Application.Features.SidewalkSlots.GetSlot;

/// <summary>SIDE-02: view a single sidewalk slot's details, including its zone's price and hours.</summary>
public sealed record GetSlotQuery(long SlotId) : IRequest<SidewalkSlotDto>;

public sealed class GetSlotQueryHandler(ISidewalkSlotRepository slots)
    : IRequestHandler<GetSlotQuery, SidewalkSlotDto>
{
    public async Task<SidewalkSlotDto> Handle(GetSlotQuery request, CancellationToken cancellationToken)
    {
        var slot = await slots.GetByIdAsync(request.SlotId, cancellationToken)
            ?? throw new NotFoundException(SideMessages.SlotNotFound);

        return slot.ToDto(distanceMeters: null);
    }
}
