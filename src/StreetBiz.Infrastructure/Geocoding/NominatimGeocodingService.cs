using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using StreetBiz.Application.Common.Interfaces;
using StreetBiz.Application.Common.Models;

namespace StreetBiz.Infrastructure.Geocoding;

public sealed class NominatimSettings
{
    public const string SectionName = "Nominatim";

    public string BaseUrl { get; init; } = "https://nominatim.openstreetmap.org";

    /// <summary>Nominatim's usage policy requires an identifying User-Agent; requests without one are refused.</summary>
    public string UserAgent { get; init; } = "StreetBiz-KLTN/1.0";

    public int TimeoutSeconds { get; init; } = 5;
}

/// <summary>
/// Forward geocoding via the free Nominatim (OpenStreetMap) API: no key, but capped at 1
/// request/second by its usage policy, so calls are throttled process-wide and successful
/// results are cached. A geocoded address is normally written straight into the row it
/// describes (BusinessRegistrations, AddressChangeRequests), so re-geocoding the same address
/// twice is redundant beyond that cache window.
/// </summary>
public sealed class NominatimGeocodingService(HttpClient httpClient, IMemoryCache cache) : IGeocodingService
{
    private static readonly SemaphoreSlim Throttle = new(1, 1);
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);
    private static DateTime lastCallUtc = DateTime.MinValue;

    private static readonly TimeSpan CacheDuration = TimeSpan.FromDays(7);

    public async Task<GeoPoint?> ForwardAsync(string address, CancellationToken cancellationToken)
    {
        var normalized = address.Trim();
        if (normalized.Length == 0)
        {
            return null;
        }

        var cacheKey = BuildCacheKey(normalized);
        if (cache.TryGetValue(cacheKey, out GeoPoint? cached))
        {
            return cached;
        }

        var result = await FetchAsync(normalized, cancellationToken);
        if (result is not null)
        {
            cache.Set(cacheKey, result, CacheDuration);
        }

        return result;
    }

    private async Task<GeoPoint?> FetchAsync(string address, CancellationToken cancellationToken)
    {
        await ThrottleAsync(cancellationToken);

        try
        {
            var url = $"search?q={Uri.EscapeDataString(address)}&format=jsonv2&limit=1";
            using var response = await httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var results = await response.Content.ReadFromJsonAsync<NominatimResult[]>(cancellationToken: cancellationToken);
            var first = results?.FirstOrDefault();
            if (first is null
                || !decimal.TryParse(first.Lat, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
                || !decimal.TryParse(first.Lon, NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
            {
                return null;
            }

            return new GeoPoint(Math.Round(lat, 6), Math.Round(lon, 6), first.DisplayName);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Geocoding assists a write; it must never fail one. The caller falls back to
            // whatever coordinates it already had (or none).
            return null;
        }
    }

    private static async Task ThrottleAsync(CancellationToken cancellationToken)
    {
        await Throttle.WaitAsync(cancellationToken);
        try
        {
            var elapsed = DateTime.UtcNow - lastCallUtc;
            if (elapsed < MinInterval)
            {
                await Task.Delay(MinInterval - elapsed, cancellationToken);
            }

            lastCallUtc = DateTime.UtcNow;
        }
        finally
        {
            Throttle.Release();
        }
    }

    private static string BuildCacheKey(string normalizedAddress)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedAddress.ToLowerInvariant()));
        return "geo:" + Convert.ToHexString(hash);
    }

    private sealed record NominatimResult(
        [property: JsonPropertyName("lat")] string Lat,
        [property: JsonPropertyName("lon")] string Lon,
        [property: JsonPropertyName("display_name")] string? DisplayName);
}
