using FluentValidation;
using MediatR;
using StreetBiz.Application.Common.Geo;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;
using StreetBiz.Application.DTOs.Sidewalk;

namespace StreetBiz.Application.Features.SidewalkSlots.SearchSlots;

/// <summary>
/// SIDE-01: browse sidewalk slots within an area. Accepts either a center point + radius
/// (sorted nearest-first) or an explicit bounding box (unsorted) — a map view naturally has
/// one or the other depending on whether the user is centered on themselves or panning.
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
    int Take = 100) : IRequest<IReadOnlyList<SidewalkSlotDto>>;

public sealed class SearchSlotsQueryValidator : AbstractValidator<SearchSlotsQuery>
{
    public SearchSlotsQueryValidator()
    {
        RuleFor(x => x)
            .Must(HaveCenterOrBoundingBox)
            .WithMessage("Provide either lat/lng/radiusMeters, or minLat/maxLat/minLng/maxLng.");

        RuleFor(x => x.RadiusMeters).GreaterThan(0).When(x => x.RadiusMeters.HasValue);
        RuleFor(x => x.Take).InclusiveBetween(1, 500);
    }

    private static bool HaveCenterOrBoundingBox(SearchSlotsQuery query)
    {
        var hasCenter = query.Lat.HasValue && query.Lng.HasValue && query.RadiusMeters.HasValue;
        var hasBox = query.MinLat.HasValue && query.MaxLat.HasValue && query.MinLng.HasValue && query.MaxLng.HasValue;
        return hasCenter || hasBox;
    }
}

public sealed class SearchSlotsQueryHandler(ISidewalkSlotRepository slots)
    : IRequestHandler<SearchSlotsQuery, IReadOnlyList<SidewalkSlotDto>>
{
    public async Task<IReadOnlyList<SidewalkSlotDto>> Handle(SearchSlotsQuery request, CancellationToken cancellationToken)
    {
        var hasCenter = request.Lat.HasValue && request.Lng.HasValue && request.RadiusMeters.HasValue;

        // SQL only narrows by bounding box — EF Core cannot translate the Haversine math in
        // GeoMath.DistanceMeters, so the exact-radius cut happens below, in memory.
        var box = request.MinLat.HasValue && request.MaxLat.HasValue && request.MinLng.HasValue && request.MaxLng.HasValue
            ? new GeoBoundingBox((double)request.MinLat.Value, (double)request.MaxLat.Value, (double)request.MinLng.Value, (double)request.MaxLng.Value)
            : GeoMath.BoundingBox((double)request.Lat!.Value, (double)request.Lng!.Value, request.RadiusMeters!.Value);

        var area = new SlotSearchArea(
            (decimal)box.MinLat, (decimal)box.MaxLat, (decimal)box.MinLon, (decimal)box.MaxLon, request.WardUnitId);

        var rows = await slots.SearchAsync(area, cancellationToken);

        double? DistanceTo(SlotRow row) => hasCenter
            ? GeoMath.DistanceMeters((double)request.Lat!.Value, (double)request.Lng!.Value, (double)row.Latitude, (double)row.Longitude)
            : null;

        var withDistance = rows.Select(r => (Row: r, Distance: DistanceTo(r)));

        if (hasCenter)
        {
            withDistance = withDistance.Where(x => x.Distance!.Value <= request.RadiusMeters!.Value);
        }

        return withDistance
            .OrderBy(x => x.Distance ?? double.MaxValue)
            .Take(request.Take)
            .Select(x => x.Row.ToDto(x.Distance))
            .ToList();
    }
}
