using MediatR;
using StreetBiz.Application.Common.Exceptions;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.Sidewalk;

namespace StreetBiz.Application.Features.SidewalkZones.GetZone;

/// <summary>A pricing zone with its ward contact, fee table and street features (technical corridors etc.).</summary>
public sealed record GetZoneQuery(int ZoneId) : IRequest<SidewalkZoneDto>;

public sealed class GetZoneQueryHandler(ISidewalkZoneRepository zones)
    : IRequestHandler<GetZoneQuery, SidewalkZoneDto>
{
    public async Task<SidewalkZoneDto> Handle(GetZoneQuery request, CancellationToken cancellationToken)
    {
        var zone = await zones.GetDetailAsync(request.ZoneId, cancellationToken)
            ?? throw new NotFoundException(SideMessages.ZoneDetailNotFound);

        return zone.ToDto();
    }
}
