namespace TOLIK.Desktop.Bridge;

/// <summary>
/// Capped exponential reconnect backoff for the Core link: 1, 2, 4, 8, 16, then a
/// hard 30 s ceiling — the overlay must always come back, but must never hammer a
/// struggling Core. The counter resets on every successful connection.
/// </summary>
public sealed class ReconnectPolicy
{
    public const double InitialSeconds = 1.0;
    public const double MaxSeconds = 30.0;

    private int _attempt;

    /// <summary>Delay before the next connection attempt (and advances the attempt counter).</summary>
    public TimeSpan NextDelay() => DelayForAttempt(_attempt++);

    /// <summary>Call after a successful connection — the next failure starts short again.</summary>
    public void Reset() => _attempt = 0;

    /// <summary>Pure delay schedule (attempt 0 → 1 s); attempts below zero clamp to the initial delay.</summary>
    public static TimeSpan DelayForAttempt(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(
            InitialSeconds * Math.Pow(2.0, Math.Max(0, attempt)),
            MaxSeconds));
}
