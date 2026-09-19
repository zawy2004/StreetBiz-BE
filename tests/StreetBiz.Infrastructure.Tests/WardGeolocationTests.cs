using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using StreetBiz.Application.Features.WardSlots;
using StreetBiz.Infrastructure.Services;

namespace StreetBiz.Infrastructure.Tests;

public sealed class WardGeolocationTests
{
    private static WardGeolocation Service(double[][][][] polygons)
    {
        var config = new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new
            {
                Geolocation = new
                {
                    Boundaries = new[] {
                new WardBoundary { WardId = 1, Version = "test-only", Polygons = polygons } }
                }
            })))).Build();
        return new(config);
    }

    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(0, 1, true)]
    [InlineData(11, 1, false)]
    [InlineData(5, 5, false)]
    public void Checks_polygon_and_hole(double latitude, double longitude, bool inside)
    {
        double[][] outer = [[0, 0], [10, 0], [10, 10], [0, 10], [0, 0]];
        double[][] hole = [[4, 4], [6, 4], [6, 6], [4, 6], [4, 4]];
        Assert.Equal(inside, Service([[outer, hole]]).Verify(1, new(latitude, longitude)).Inside);
    }

    [Fact]
    public void Supports_disjoint_polygons()
    {
        var service = Service([
            [[[0, 0], [2, 0], [2, 2], [0, 2], [0, 0]]],
            [[[10, 10], [12, 10], [12, 12], [10, 12], [10, 10]]]]);
        Assert.True(service.Verify(1, new(11, 11)).Inside);
        Assert.False(service.Verify(1, new(6, 6)).Inside);
    }

    [Theory]
    [InlineData(91, 0)]
    [InlineData(0, 181)]
    [InlineData(double.NaN, 0)]
    public void Rejects_invalid_coordinate(double latitude, double longitude) =>
        Assert.Equal(400, Assert.Throws<WardException>(() => Service([]).Verify(1, new(latitude, longitude))).Status);

    [Fact]
    public void Missing_boundary_is_not_assumed_valid() =>
        Assert.Equal(503, Assert.Throws<WardException>(() => Service([]).Verify(1, new(1, 1))).Status);

    [Fact]
    public void Self_intersecting_boundary_is_rejected() =>
        Assert.Equal(503, Assert.Throws<WardException>(() =>
            Service([[[[0, 0], [2, 2], [0, 2], [2, 0], [0, 0]]]]).Verify(1, new(1, 1))).Status);
}
