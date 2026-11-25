using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using HttpMcpProxy.Configuration;

namespace HttpMcpProxy.Execution;

internal static class RateLimiterFactory
{
    public static RateLimiter? Create(RateLimitConfig config)
    {
        if (config.Requests is not { } requests || requests <= 0)
            return null;

        var period = config.Per switch
        {
            "second" => TimeSpan.FromSeconds(1),
            "minute" => TimeSpan.FromMinutes(1),
            "hour" => TimeSpan.FromHours(1),
            "day" => TimeSpan.FromDays(1),
            _ => throw new ConfigException($"rate_limit.per must be second|minute|hour|day, got '{config.Per}'"),
        };

        // Refill continuously (one token at a time) so the configured rate is spread over the period.
        var burst = Math.Max(1, config.Burst ?? requests);
        return new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = burst,
            TokensPerPeriod = 1,
            ReplenishmentPeriod = period / requests,
            QueueLimit = config.OnLimit == "wait" ? int.MaxValue : 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true,
        });
    }
}

internal sealed class CircuitBreaker(CircuitBreakerConfig config)
{
    private readonly Lock _lock = new();
    private int _failures;
    private DateTimeOffset? _openedAt;
    private bool _trialInFlight;

    public bool TryEnter(out TimeSpan retryIn)
    {
        retryIn = TimeSpan.Zero;
        if (!config.Enabled) return true;

        lock (_lock)
        {
            if (_openedAt is not { } openedAt) return true;

            var elapsed = DateTimeOffset.UtcNow - openedAt;
            var reset = TimeSpan.FromMilliseconds(config.ResetAfterMs);
            if (elapsed >= reset && !_trialInFlight)
            {
                _trialInFlight = true; // half-open: let exactly one request through
                return true;
            }

            retryIn = elapsed >= reset ? TimeSpan.FromSeconds(1) : reset - elapsed;
            return false;
        }
    }

    public void Record(bool success)
    {
        if (!config.Enabled) return;
        lock (_lock)
        {
            _trialInFlight = false;
            if (success)
            {
                _failures = 0;
                _openedAt = null;
            }
            else if (++_failures >= config.FailureThreshold || _openedAt is not null)
            {
                _openedAt = DateTimeOffset.UtcNow;
            }
        }
    }
}

internal sealed record CachedResponse(int Status, string? ReasonPhrase, Dictionary<string, string> Headers, string? ContentType, byte[] Body, bool Truncated);

internal sealed class ResponseCache(int maxEntries)
{
    private readonly ConcurrentDictionary<string, (CachedResponse Response, DateTimeOffset Expires)> _entries = new();

    public bool TryGet(string key, out CachedResponse response)
    {
        if (_entries.TryGetValue(key, out var entry) && entry.Expires > DateTimeOffset.UtcNow)
        {
            response = entry.Response;
            return true;
        }

        _entries.TryRemove(key, out _);
        response = null!;
        return false;
    }

    public void Set(string key, CachedResponse response, TimeSpan ttl)
    {
        if (ttl <= TimeSpan.Zero) return;
        if (_entries.Count >= maxEntries)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var (k, v) in _entries)
                if (v.Expires <= now) _entries.TryRemove(k, out _);
            // Still full: evict the entries closest to expiry.
            foreach (var victim in _entries.OrderBy(e => e.Value.Expires).Take(_entries.Count - maxEntries + 1))
                _entries.TryRemove(victim.Key, out _);
        }

        _entries[key] = (response, DateTimeOffset.UtcNow + ttl);
    }
}
