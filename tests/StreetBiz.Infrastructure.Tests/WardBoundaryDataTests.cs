using Microsoft.Extensions.Configuration;
using NetTopologySuite.Geometries;
using StreetBiz.Infrastructure.Services;

namespace StreetBiz.Infrastructure.Tests;

/// <summary>
/// The shipped Đà Nẵng ward boundaries (OpenStreetMap) are usable: every polygon is valid, each
/// ward appears once, neighbouring wards do not overlap, and the geolocation service reads them.
/// </summary>
public sealed class WardBoundaryDataTests
{
    private static readonly IReadOnlyList<WardBoundary> Boundaries = WardGeolocation.ReadFile(WardGeolocation.DefaultBoundariesFile);

    [Fact]
    public void Covers_the_wards_of_da_nang_once_each_with_a_version()
    {
        Assert.True(Boundaries.Count >= 90, $"only {Boundaries.Count} wards in the file");
        Assert.Equal(Boundaries.Count, Boundaries.Select(b => b.WardId).Distinct().Count());
        Assert.All(Boundaries, b => Assert.StartsWith("osm-relation-", b.Version));
        Assert.All(Boundaries, b => Assert.InRange(b.WardId, 10, 190));
    }

    [Fact]
    public void Every_boundary_is_a_valid_shape_inside_da_nang()
    {
        // Đà Nẵng after 1/7/2025 (with former Quảng Nam), mainland and Cù Lao Chàm.
        var region = new Envelope(107.0, 109.0, 14.8, 16.4);
        foreach (var boundary in Boundaries)
        {
            var shape = WardGeolocation.ToShape(boundary);
            Assert.True(region.Contains(shape.EnvelopeInternal), $"{boundary.Name} lies outside Đà Nẵng");
        }
    }

    [Fact]
    public void Neighbouring_wards_overlap_by_less_than_one_percent()
    {
        var shapes = Boundaries.Select(b => (b.Name, Shape: WardGeolocation.ToShape(b))).ToList();
        for (var i = 0; i < shapes.Count; i++)
        {
            for (var j = i + 1; j < shapes.Count; j++)
            {
                var (a, b) = (shapes[i].Shape, shapes[j].Shape);
                if (!a.EnvelopeInternal.Intersects(b.EnvelopeInternal)) continue;
                var shared = a.Intersection(b).Area;
                Assert.True(shared < 0.01 * Math.Min(a.Area, b.Area),
                    $"{shapes[i].Name} and {shapes[j].Name} overlap by {shared / Math.Min(a.Area, b.Area):P1}");
            }
        }
    }

    [Fact]
    public void Each_wards_own_interior_point_is_inside_it_and_in_no_other_ward()
    {
        var service = new WardGeolocation(new ConfigurationBuilder().Build());
        foreach (var boundary in Boundaries)
        {
            var inside = WardGeolocation.ToShape(boundary).InteriorPoint;
            var result = service.Verify(boundary.WardId, new(inside.Y, inside.X));
            Assert.True(result.Inside, $"{boundary.Name}: its interior point is outside");
            Assert.Equal(boundary.Version, result.BoundaryVersion);
        }
    }

    [Fact]
    public void A_ward_without_osm_data_still_answers_503_rather_than_guessing()
    {
        var missing = Enumerable.Range(10, 181).First(id => Boundaries.All(b => b.WardId != id));
        var service = new WardGeolocation(new ConfigurationBuilder().Build());
        Assert.Equal(503, Assert.Throws<StreetBiz.Application.Features.WardSlots.WardException>(
            () => service.Verify(missing, new(16.05, 108.2))).Status);
    }
}
