using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;

namespace StreetBiz.Infrastructure.Geocoding;

/// <summary>
/// Esri's key-free World Geocoding Service, limited to Đà Nẵng -- the same provider and extent the
/// web (services/map/forward-geocode.ts) and app use for address search, because
/// nominatim.openstreetmap.org is unreachable from the team's network (confirmed again 22/10/2026).
/// Results are only candidates the user confirms on a map.
/// </summary>
public sealed class EsriGeocodingService(HttpClient httpClient, IMemoryCache cache)
{
    public const string BaseUrl = "https://geocode.arcgis.com/arcgis/rest/services/World/GeocodeServer/";

    // xmin,ymin,xmax,ymax (lon/lat), as on the web: a bare street name otherwise matches other provinces.
    private const string DaNangExtent = "107.85,15.9,108.35,16.2";
    private const string DaNangCenter = "108.206,16.047";
    private const double MinScore = 60;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromDays(7);

    public async Task<IReadOnlyList<GeoPoint>> SearchAsync(string address, int limit, CancellationToken cancellationToken)
    {
        var text = address.Trim();
        if (text.Length == 0)
        {
            return [];
        }

        var max = Math.Clamp(limit, 1, 10);
        var cacheKey = $"esri-geocode:{max}:{text.ToLowerInvariant()}";
        if (cache.TryGetValue(cacheKey, out IReadOnlyList<GeoPoint>? cached) && cached is not null)
        {
            return cached;
        }

        try
        {
            var url = "findAddressCandidates?" + string.Join('&', new Dictionary<string, string>
            {
                ["SingleLine"] = text,
                ["f"] = "json",
                ["maxLocations"] = (max + 3).ToString(CultureInfo.InvariantCulture),
                ["countryCode"] = "VNM",
                ["searchExtent"] = DaNangExtent,
                ["location"] = DaNangCenter,
                // The full label carries the ward/district ("…, Hải Châu, Đà Nẵng"), which the
                // short "address" ("Chợ Cồn") does not.
                ["outFields"] = "LongLabel",
            }.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
            using var response = await httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var body = await response.Content.ReadFromJsonAsync<EsriResponse>(cancellationToken: cancellationToken);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var points = new List<GeoPoint>();
            foreach (var candidate in body?.Candidates ?? [])
            {
                var raw = candidate.Attributes?.LongLabel is { Length: > 0 } longLabel ? longLabel : candidate.Address;
                if (candidate.Score < MinScore || candidate.Location is null || string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                // "…, Đà Nẵng, 50312, VNM" -> "…, Đà Nẵng": a postcode makes one street look like several.
                var label = Regex.Replace(Regex.Replace(raw, @",\s*VNM$", string.Empty), @",\s*\d{5}(?=,|$)", string.Empty);
                if (!seen.Add(label))
                {
                    continue;
                }

                points.Add(new GeoPoint(
                    Math.Round((decimal)candidate.Location.Y, 6),
                    Math.Round((decimal)candidate.Location.X, 6),
                    label));
                if (points.Count == max)
                {
                    break;
                }
            }

            cache.Set(cacheKey, (IReadOnlyList<GeoPoint>)points, CacheDuration);
            return points;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return [];
        }
    }

    private sealed record EsriResponse([property: JsonPropertyName("candidates")] List<EsriCandidate>? Candidates);

    private sealed record EsriCandidate(
        [property: JsonPropertyName("address")] string? Address,
        [property: JsonPropertyName("score")] double Score,
        [property: JsonPropertyName("location")] EsriPoint? Location,
        [property: JsonPropertyName("attributes")] EsriAttributes? Attributes);

    private sealed record EsriAttributes([property: JsonPropertyName("LongLabel")] string? LongLabel);

    private sealed record EsriPoint([property: JsonPropertyName("x")] double X, [property: JsonPropertyName("y")] double Y);
}

/// <summary>
/// The app's IGeocodingService. Interactive search (candidates a person confirms on a map) asks
/// Esri first -- fast and reachable everywhere the clients run -- then Nominatim. A forward lookup
/// whose result is written into a row keeps Nominatim (open data) first, with Esri only as a
/// fallback when Nominatim gives nothing.
/// </summary>
public sealed class FallbackGeocodingService(NominatimGeocodingService nominatim, EsriGeocodingService esri) : IGeocodingService
{
    public async Task<GeoPoint?> ForwardAsync(string address, CancellationToken cancellationToken) =>
        await nominatim.ForwardAsync(address, cancellationToken)
        ?? (await esri.SearchAsync(address, 1, cancellationToken)).FirstOrDefault();

    public async Task<IReadOnlyList<GeoPoint>> SearchAsync(string address, int limit, CancellationToken cancellationToken)
    {
        var fromEsri = await esri.SearchAsync(address, limit, cancellationToken);
        return fromEsri.Count > 0 ? fromEsri : await nominatim.SearchAsync(address, limit, cancellationToken);
    }
}
