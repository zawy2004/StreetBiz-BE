using StreetBiz.Application.Common.Models;

namespace StreetBiz.Application.Common.Interfaces;

/// <summary>
/// SYS-01. Wraps whichever forward-geocoding provider is configured (Nominatim by default) so
/// callers never depend on a specific vendor. Failures (timeout, no match, provider outage)
/// return null rather than throwing -- geocoding assists a write, it never blocks one.
/// </summary>
public interface IGeocodingService
{
    Task<GeoPoint?> ForwardAsync(string address, CancellationToken cancellationToken);
}
