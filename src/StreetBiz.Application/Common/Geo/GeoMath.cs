namespace StreetBiz.Application.Common.Geo;

/// <summary>
/// Pure distance/bounding-box math for slot coordinates. The database stores
/// latitude/longitude as plain DECIMAL(9,6) (no spatial type), so proximity queries
/// filter coarsely in SQL by bounding box, then refine in memory with Haversine —
/// EF Core cannot translate Math.Atan2/Math.Asin to T-SQL.
/// </summary>
public static class GeoMath
{
    private const double EarthRadiusMeters = 6_371_000d;
    private const double MetersPerDegreeLatitude = 111_320d;

    /// <summary>Great-circle distance between two points, in meters.</summary>
    public static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        var dLat = ToRadians(lat2 - lat1);
        var dLon = ToRadians(lon2 - lon1);
        var a =
            Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
            Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) *
            Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return EarthRadiusMeters * c;
    }

    /// <summary>
    /// A bounding box guaranteed to contain every point within <paramref name="radiusMeters"/>
    /// of the center. It over-includes near the poles/date line on purpose — callers must
    /// still apply <see cref="DistanceMeters"/> to exclude points outside the actual circle.
    /// </summary>
    public static GeoBoundingBox BoundingBox(double lat, double lon, double radiusMeters)
    {
        var latDelta = radiusMeters / MetersPerDegreeLatitude;
        var metersPerDegreeLongitude = MetersPerDegreeLatitude * Math.Cos(ToRadians(lat));
        // Near the poles cos(lat) approaches 0, which would blow the longitude delta up
        // to +-180. Slots only ever exist in inhabited latitudes, so clamp defensively.
        var lonDelta = metersPerDegreeLongitude > 1
            ? radiusMeters / metersPerDegreeLongitude
            : 180d;

        return new GeoBoundingBox(
            MinLat: lat - latDelta,
            MaxLat: lat + latDelta,
            MinLon: lon - lonDelta,
            MaxLon: lon + lonDelta);
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180d;
}

/// <summary>A rectangular search area in latitude/longitude degrees.</summary>
public sealed record GeoBoundingBox(double MinLat, double MaxLat, double MinLon, double MaxLon);
