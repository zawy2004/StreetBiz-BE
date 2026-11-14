using System.Collections.Concurrent;

namespace StreetBiz.Infrastructure.Security;

/// <summary>
/// SMS-pumping defence: counts the OTP messages one client address caused within a sliding window.
/// The per-phone limits in OtpService stop one number being flooded; this stops one address asking
/// for codes to many different numbers (each costs money). In memory and per process, which matches
/// the single API instance the deployment runs; a restart simply forgets the counts.
/// </summary>
public sealed class OtpIpGuard
{
    private const int PruneAbove = 10_000;

    private readonly ConcurrentDictionary<string, Queue<DateTime>> hits = new();

    /// <summary>Records one send for <paramref name="ip"/> and returns true, or false when the limit is reached.</summary>
    public bool TryAcquire(string? ip, DateTime now, int limit, TimeSpan window)
    {
        if (limit <= 0 || string.IsNullOrEmpty(ip))
        {
            return true;
        }

        if (hits.Count > PruneAbove)
        {
            Prune(now, window);
        }

        var queue = hits.GetOrAdd(ip, _ => new Queue<DateTime>());
        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek() >= window)
            {
                queue.Dequeue();
            }

            if (queue.Count >= limit)
            {
                return false;
            }

            queue.Enqueue(now);
            return true;
        }
    }

    /// <summary>Gives a send back, used when the SMS could not be delivered so it should not count.</summary>
    public void Release(string? ip)
    {
        if (!string.IsNullOrEmpty(ip) && hits.TryGetValue(ip, out var queue))
        {
            lock (queue)
            {
                if (queue.Count > 0)
                {
                    // The newest entry is the one just recorded.
                    var kept = queue.ToArray();
                    queue.Clear();
                    foreach (var at in kept.Take(kept.Length - 1))
                    {
                        queue.Enqueue(at);
                    }
                }
            }
        }
    }

    private void Prune(DateTime now, TimeSpan window)
    {
        foreach (var (ip, queue) in hits)
        {
            lock (queue)
            {
                while (queue.Count > 0 && now - queue.Peek() >= window)
                {
                    queue.Dequeue();
                }

                if (queue.Count == 0)
                {
                    hits.TryRemove(ip, out _);
                }
            }
        }
    }
}
