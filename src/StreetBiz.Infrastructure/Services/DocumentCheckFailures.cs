using System.Collections.Concurrent;

namespace StreetBiz.Infrastructure.Services;

/// <summary>
/// Registration + input fingerprint -> when its last failed AI document check happened and when a new
/// one may run. A singleton: WardComplianceService is scoped, and the point is to stop repeated clicks
/// (and every officer of the ward) from spending provider quota while the provider is down. In memory,
/// so it is per API instance and forgotten on restart -- the cost of forgetting is one more attempt.
/// </summary>
public sealed class DocumentCheckFailures
{
    private readonly ConcurrentDictionary<string, (DateTime FailedAt, DateTime RetryAfter)> failures = new();

    public bool TryGet(string key, DateTime nowUtc, out (DateTime FailedAt, DateTime RetryAfter) failure)
    {
        if (failures.TryGetValue(key, out failure) && nowUtc < failure.RetryAfter)
        {
            return true;
        }

        failures.TryRemove(key, out _);
        return false;
    }

    public void Record(string key, DateTime failedAtUtc, DateTime retryAfterUtc) =>
        failures[key] = (failedAtUtc, retryAfterUtc);

    public void Clear(string key) => failures.TryRemove(key, out _);
}
