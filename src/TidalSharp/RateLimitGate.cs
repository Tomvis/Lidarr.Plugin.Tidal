#nullable enable
using System;
using System.Globalization;

namespace TidalSharp;

// Shared 429 cooldown for every Tidal API call (Tomvis fork, MUSIC-113). Upstream retried a 429
// forever after 100-1000 ms; with several searches doing that at once Tidal never let go and
// Lidarr's search threads hung for 15+ minutes. Now a 429 backs off (Retry-After, else 1/2/4/8 s),
// every caller waits out the same cooldown, and API.Call gives up after MaxAttempts.
public class RateLimitGate
{
    public const int MaxAttempts = 4;
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

    private readonly Func<DateTime> _now;
    private readonly object _lock = new();
    private DateTime _until = DateTime.MinValue;

    public RateLimitGate(Func<DateTime>? now = null) => _now = now ?? (() => DateTime.UtcNow);

    public bool IsCoolingDown => Remaining > TimeSpan.Zero;

    public TimeSpan Remaining
    {
        get
        {
            lock (_lock)
            {
                var left = _until - _now();
                return left > TimeSpan.Zero ? left : TimeSpan.Zero;
            }
        }
    }

    // Records a 429 on the given (1-based) attempt and returns how long to wait before retrying.
    public TimeSpan Trip(int attempt, string? retryAfter)
    {
        var now = _now();
        var delay = ParseRetryAfter(retryAfter, now) ?? TimeSpan.FromSeconds(Math.Pow(2, Math.Min(attempt, 10) - 1));
        if (delay > MaxDelay)
            delay = MaxDelay;

        lock (_lock)
        {
            if (now + delay > _until)
                _until = now + delay;
            return _until - now;
        }
    }

    private static TimeSpan? ParseRetryAfter(string? value, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            return TimeSpan.FromSeconds(seconds);

        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at))
            return at > now ? at - now : TimeSpan.Zero;

        return null;
    }
}
