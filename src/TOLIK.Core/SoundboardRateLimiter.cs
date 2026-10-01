using System.Collections.Concurrent;

namespace TOLIK.Core;

/// <summary>
/// Per-user rate limiter for reaction sounds (AGENTS.md §17: playback must be
/// rate limited — never annoying). Sliding window: one sound per interval,
/// bursts rejected with 429. Deterministic, no LLM involvement.
/// </summary>
public sealed class SoundboardRateLimiter
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(10);
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastPlayed = new();

    /// <summary>Returns true when the user may play a sound right now.</summary>
    public bool TryAcquire(Guid userId)
    {
        if (userId == Guid.Empty) return false;
        var now = DateTimeOffset.UtcNow;
        var cutoff = now - Window;

        // Single-flight check-and-set: first writer wins the window.
        while (true)
        {
            if (!_lastPlayed.TryGetValue(userId, out var last) || last <= cutoff)
            {
                if (_lastPlayed.TryAdd(userId, now)) return true;
                continue; // raced with another thread; re-check
            }
            return false;
        }
    }

    /// <summary>Prunes stale entries to keep memory bounded.</summary>
    public void Prune()
    {
        var cutoff = DateTimeOffset.UtcNow - Window;
        foreach (var entry in _lastPlayed)
            if (entry.Value <= cutoff)
                _lastPlayed.TryRemove(entry.Key, out _);
    }

    /// <summary>Seconds remaining until the user can play again (0 = can play now).</summary>
    public double SecondsRemaining(Guid userId)
    {
        if (!_lastPlayed.TryGetValue(userId, out var last)) return 0;
        var remaining = (last + Window - DateTimeOffset.UtcNow).TotalSeconds;
        return remaining > 0 ? remaining : 0;
    }
}
