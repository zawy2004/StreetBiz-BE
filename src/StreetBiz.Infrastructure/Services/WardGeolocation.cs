using Microsoft.Extensions.Configuration;
using NetTopologySuite.Geometries;
using StreetBiz.Application.Features.WardSlots;

namespace StreetBiz.Infrastructure.Services;

// Config coordinates use GeoJSON order: longitude, latitude. No guessed ward boundaries.
public sealed class WardBoundary
{
    public int WardId { get; set; }
    public string Version { get; set; } = "";
    public double[][][][] Polygons { get; set; } = [];
}

public sealed class WardGeolocation(IConfiguration configuration) : IGeolocation
{
    public GeofenceResult Verify(int wardId, GeoPoint point)
    {
        WardRules.Coordinates(point);
        var matches = configuration.GetSection("Geolocation:Boundaries").Get<WardBoundary[]>()?
            .Where(x => x.WardId == wardId).ToArray() ?? [];
        if (matches.Length > 1)
            throw new WardException(503, "boundary_invalid", "Có nhiều cấu hình ranh giới cho cùng một phường.");
        var boundary = matches.SingleOrDefault();
        if (boundary is null || boundary.Polygons.Length == 0 || string.IsNullOrWhiteSpace(boundary.Version))
            throw new WardException(503, "boundary_unavailable", "Chưa cấu hình ranh giới đã xác minh cho phường này.");
        try
        {
            var factory = new GeometryFactory(new PrecisionModel(), 4326);
            LinearRing Ring(double[][] points)
            {
                if (points.Length < 4 || points.Any(p => p.Length != 2 || !double.IsFinite(p[0]) ||
                    !double.IsFinite(p[1]) || p[0] is < -180 or > 180 || p[1] is < -90 or > 90))
                    throw new ArgumentException("Invalid boundary ring.");
                return factory.CreateLinearRing(points.Select(p => new Coordinate(p[0], p[1])).ToArray());
            }
            var polygons = boundary.Polygons.Select(rings => factory.CreatePolygon(
                Ring(rings[0]), rings.Skip(1).Select(Ring).ToArray())).ToArray();
            var shape = factory.CreateMultiPolygon(polygons);
            if (!shape.IsValid || shape.IsEmpty) throw new ArgumentException("Invalid boundary geometry.");
            return new(shape.Covers(factory.CreatePoint(new Coordinate(point.Longitude, point.Latitude))), wardId, boundary.Version);
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException)
        {
            throw new WardException(503, "boundary_invalid", "Cấu hình ranh giới phường không hợp lệ.");
        }
    }
}
