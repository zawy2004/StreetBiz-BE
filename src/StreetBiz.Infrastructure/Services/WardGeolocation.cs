using Microsoft.Extensions.Configuration;
using NetTopologySuite.Geometries;
using StreetBiz.Application.Features.WardSlots;

namespace StreetBiz.Infrastructure.Services;

// Config coordinates use GeoJSON order: longitude, latitude. No guessed ward boundaries.
public sealed class WardBoundary
{
    /// <summary>Ward name, for people reading the file; matching is by <see cref="WardId"/>.</summary>
    public string? Name { get; set; }

    public int WardId { get; set; }
    public string Version { get; set; } = "";
    public double[][][][] Polygons { get; set; } = [];
}

/// <summary>
/// SYS-02 ward containment. Boundaries come from <c>Geolocation:Boundaries</c> in configuration
/// and from the JSON file named by <c>Geolocation:BoundariesFile</c> (default
/// <c>Data/ward-boundaries.da-nang.json</c> next to the app), a configured ward taking precedence
/// over the file. Each ward's geometry is built once and cached; a missing or invalid boundary
/// still answers 503 rather than assuming the point is inside.
/// </summary>
public sealed class WardGeolocation(IConfiguration configuration) : IGeolocation
{
    public const string DefaultBoundariesFile = "Data/ward-boundaries.da-nang.json";

    private readonly Lazy<IReadOnlyDictionary<int, WardBoundary[]>> boundaries = new(() => Load(configuration));
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, (MultiPolygon? Shape, string Version, WardException? Error)> shapes = new();

    public GeofenceResult Verify(int wardId, GeoPoint point)
    {
        WardRules.Coordinates(point);
        var (shape, version, error) = shapes.GetOrAdd(wardId, Build);
        if (error is not null)
            throw error;
        return new(shape!.Covers(shape.Factory.CreatePoint(new Coordinate(point.Longitude, point.Latitude))), wardId, version);
    }

    private (MultiPolygon?, string, WardException?) Build(int wardId)
    {
        var matches = boundaries.Value.TryGetValue(wardId, out var found) ? found : [];
        if (matches.Length > 1)
            return (null, "", new WardException(503, "boundary_invalid", "Có nhiều cấu hình ranh giới cho cùng một phường."));
        var boundary = matches.SingleOrDefault();
        if (boundary is null || boundary.Polygons.Length == 0 || string.IsNullOrWhiteSpace(boundary.Version))
            return (null, "", new WardException(503, "boundary_unavailable", "Chưa cấu hình ranh giới đã xác minh cho phường này."));
        try
        {
            return (ToShape(boundary), boundary.Version, null);
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException)
        {
            return (null, "", new WardException(503, "boundary_invalid", "Cấu hình ranh giới phường không hợp lệ."));
        }
    }

    /// <summary>Builds a valid MultiPolygon or throws <see cref="ArgumentException"/>.</summary>
    public static MultiPolygon ToShape(WardBoundary boundary)
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
        return shape;
    }

    private static IReadOnlyDictionary<int, WardBoundary[]> Load(IConfiguration configuration)
    {
        var configured = configuration.GetSection("Geolocation:Boundaries").Get<WardBoundary[]>() ?? [];
        var fromFile = ReadFile(configuration["Geolocation:BoundariesFile"] ?? DefaultBoundariesFile);
        var configuredWards = configured.Select(x => x.WardId).ToHashSet();
        return configured.Concat(fromFile.Where(x => !configuredWards.Contains(x.WardId)))
            .GroupBy(x => x.WardId)
            .ToDictionary(g => g.Key, g => g.ToArray());
    }

    /// <summary>The boundaries file (<see cref="WardBoundaryFile"/>), or none when it is absent.</summary>
    public static IReadOnlyList<WardBoundary> ReadFile(string path)
    {
        var full = Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
        if (!File.Exists(full))
            return [];
        using var stream = File.OpenRead(full);
        return System.Text.Json.JsonSerializer.Deserialize<WardBoundaryFile>(stream,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))?.Boundaries ?? [];
    }
}

/// <summary>Shape of the boundaries file: provenance plus one entry per ward.</summary>
public sealed class WardBoundaryFile
{
    public string Source { get; set; } = "";
    public string License { get; set; } = "";
    public string Retrieved { get; set; } = "";
    public List<WardBoundary> Boundaries { get; set; } = [];
}
