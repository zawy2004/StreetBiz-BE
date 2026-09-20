using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Geo;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.DTOs.Sidewalk;

namespace StreetBiz.Application.Features.SidewalkSlots.SearchSlots;

/// <summary>
/// SIDE-01: browse sidewalk slots within an area. Accepts a center point + radius (sorted
/// nearest-first), an explicit bounding box (unsorted), or a zoneId (a whole street, for the
/// street-strip diagram) -- a map view naturally has one of the first two depending on whether
/// the user is centered on themselves or panning; a diagram already knows which street it wants.
/// </summary>
public sealed record SearchSlotsQuery(
    decimal? Lat,
    decimal? Lng,
    double? RadiusMeters,
    decimal? MinLat,
    decimal? MaxLat,
    decimal? MinLng,
    decimal? MaxLng,
    int? WardUnitId,
    int? ZoneId,
    bool IncludeUnavailable = false,
    int Take = 100) : IRequest<IReadOnlyList<SidewalkSlotDto>>;

public sealed class SearchSlotsQueryValidator : AbstractValidator<SearchSlotsQuery>
{
    public SearchSlotsQueryValidator()
    {
        RuleFor(x => x)
            .Must(HaveCenterOrBoundingBoxOrZone)
            .WithMessage("Provide either lat/lng/radiusMeters, minLat/maxLat/minLng/maxLng, or zoneId.");

        RuleFor(x => x.RadiusMeters).GreaterThan(0).When(x => x.RadiusMeters.HasValue);
        RuleFor(x => x.ZoneId).GreaterThan(0).When(x => x.ZoneId.HasValue);
        RuleFor(x => x.Take).InclusiveBetween(1, 500);
    }

    private static bool HaveCenterOrBoundingBoxOrZone(SearchSlotsQuery query)
    {
        var hasCenter = query.Lat.HasValue && query.Lng.HasValue && query.RadiusMeters.HasValue;
        var hasBox = query.MinLat.HasValue && query.MaxLat.HasValue && query.MinLng.HasValue && query.MaxLng.HasValue;
        return hasCenter || hasBox || query.ZoneId.HasValue;
    }
}

public sealed class SearchSlotsQueryHandler(ISidewalkSlotRepository slots)
    : IRequestHandler<SearchSlotsQuery, IReadOnlyList<SidewalkSlotDto>>
{
    public async Task<IReadOnlyList<SidewalkSlotDto>> Handle(SearchSlotsQuery request, CancellationToken cancellationToken)
    {
        var hasCenter = request.Lat.HasValue && request.Lng.HasValue && request.RadiusMeters.HasValue;
        var hasBox = request.MinLat.HasValue && request.MaxLat.HasValue && request.MinLng.HasValue && request.MaxLng.HasValue;

        // SQL only narrows by bounding box — EF Core cannot translate the Haversine math in
        // GeoMath.DistanceMeters, so the exact-radius cut happens below, in memory. A zoneId
        // query has no area at all: the whole street is wanted, not a viewport.
        GeoBoundingBox? box = hasBox
            ? new GeoBoundingBox((double)request.MinLat!.Value, (double)request.MaxLat!.Value, (double)request.MinLng!.Value, (double)request.MaxLng!.Value)
            : hasCenter
                ? GeoMath.BoundingBox((double)request.Lat!.Value, (double)request.Lng!.Value, request.RadiusMeters!.Value)
                : null;

        var area = new SlotSearchArea(
            (decimal?)box?.MinLat, (decimal?)box?.MaxLat, (decimal?)box?.MinLon, (decimal?)box?.MaxLon,
            request.WardUnitId, request.ZoneId, request.IncludeUnavailable);

        var rows = await slots.SearchAsync(area, cancellationToken);

        double? DistanceTo(SlotRow row) => hasCenter
            ? GeoMath.DistanceMeters((double)request.Lat!.Value, (double)request.Lng!.Value, (double)row.Latitude, (double)row.Longitude)
            : null;

        var withDistance = rows.Select(r => (Row: r, Distance: DistanceTo(r)));

        if (hasCenter)
        {
            withDistance = withDistance.Where(x => x.Distance!.Value <= request.RadiusMeters!.Value);
        }

        // With no center, every distance is null and the OrderBy key ties — the ThenBy keeps
        // which rows survive Take deterministic instead of depending on SQL's return order.
        return withDistance
            .OrderBy(x => x.Distance ?? double.MaxValue)
            .ThenBy(x => x.Row.SlotId)
            .Take(request.Take)
            .Select(x => x.Row.ToDto(x.Distance))
            .ToList();
    }
}
