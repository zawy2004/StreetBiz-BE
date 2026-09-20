using FluentAssertions;
using StreetBiz.Application.Common.Geo;

namespace StreetBiz.Application.Tests;

public sealed class GeoMathTests
{
    // Hoa Quy pilot ward, Da Nang: two of the seeded slots (HQ-DH-01, HQ-TC-01), roughly
    // 480m apart in a straight line per a map measurement of the seed coordinates.
    private const double HqDh01Lat = 16.0132;
    private const double HqDh01Lon = 108.2398;
    private const double HqTc01Lat = 16.0155;
    private const double HqTc01Lon = 108.2437;

    [Fact]
    public void DistanceMeters_matches_a_known_two_point_measurement()
    {
        var distance = GeoMath.DistanceMeters(HqDh01Lat, HqDh01Lon, HqTc01Lat, HqTc01Lon);

        // Independently computed (great-circle) reference distance for these coordinates.
        distance.Should().BeApproximately(489, 1);
    }

    [Fact]
    public void DistanceMeters_from_a_point_to_itself_is_zero()
    {
        GeoMath.DistanceMeters(HqDh01Lat, HqDh01Lon, HqDh01Lat, HqDh01Lon).Should().Be(0);
    }

    [Fact]
    public void BoundingBox_contains_every_point_within_the_radius()
    {
        const double radiusMeters = 500;
        var box = GeoMath.BoundingBox(HqDh01Lat, HqDh01Lon, radiusMeters);

        // A point exactly at the known distance below the radius must fall inside the box.
        box.MinLat.Should().BeLessThan(HqTc01Lat);
        box.MaxLat.Should().BeGreaterThan(HqTc01Lat);
        box.MinLon.Should().BeLessThan(HqTc01Lon);
        box.MaxLon.Should().BeGreaterThan(HqTc01Lon);
    }

    [Fact]
    public void BoundingBox_alone_over_includes_a_point_outside_the_circle()
    {
        // The whole point of GeoMath: a bbox is a coarse SQL pre-filter, not the final answer.
        // A point on the bbox's diagonal corner is inside the box but outside the circle.
        const double radiusMeters = 100;
        var box = GeoMath.BoundingBox(HqDh01Lat, HqDh01Lon, radiusMeters);

        var cornerLat = box.MaxLat;
        var cornerLon = box.MaxLon;
        var distance = GeoMath.DistanceMeters(HqDh01Lat, HqDh01Lon, cornerLat, cornerLon);

        distance.Should().BeGreaterThan(radiusMeters);
    }
}
