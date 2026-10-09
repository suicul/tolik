using System.Collections.Concurrent;

namespace TOLIK.Contracts;

/// <summary>
/// One presence node (docs/identity-presence.md §5, IP-4): a device TOLIK currently
/// inhabits or is aware of. ONE TOLIK — multiple organs, never clones; the Identity
/// is Core-backed and single. This is display/routing state — it NEVER grants
/// authority (Attention ≠ Authority: proximity/loudness/visibility give no permission).
/// </summary>
public sealed record PresenceNode(
    string DeviceId,
    string DeviceName,
    string? Location,
    string Mode,
    string? Activity,
    bool Available,
    IReadOnlyList<string> Capabilities,
    DateTimeOffset LastSeen)
{
    /// <summary>Presence priority tiers (docs/identity-presence.md §5 policy): lower = more important.</summary>
    public const int PriorityActiveConversation = 0;
    public const int PriorityInterlocutorDevice = 1;
    public const int PriorityTaskDevice = 2;
    public const int PriorityOwnerDevice = 3;
    public const int PriorityFamilyDevice = 4;
    public const int PriorityQuest = 5;
    public const int PriorityHome = 6;
    public const int PriorityFallback = 7;
}

/// <summary>
/// Presence Manager (docs/identity-presence.md §5, IP-4): tracks where TOLIK currently
/// exists — which devices he inhabits/observes, what each is doing, and where he is
/// needed. ONE TOLIK: the registry is per-device state under one Core-backed Identity.
/// PresenceLost: a device whose LastSeen expired becomes unavailable — the Core does
/// NOT die when presence is lost (persistent self, replaceable organs).
/// Deterministic, display/routing only — no security authority whatsoever.
/// </summary>
public sealed class PresenceManager
{
    /// <summary>Unavailability threshold: a node silent for longer is PresenceLost.</summary>
    public static readonly TimeSpan AvailabilityTimeout = TimeSpan.FromSeconds(90);

    private readonly ConcurrentDictionary<string, PresenceNode> _nodes = new();

    /// <summary>Registers or refreshes a device's presence (heartbeat/report driven).</summary>
    public PresenceNode Register(string deviceId, string deviceName, string? location,
        string mode, string? activity, IReadOnlyList<string> capabilities, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) throw new ArgumentException("Device id is required.", nameof(deviceId));
        var previous = _nodes.TryGetValue(deviceId, out var p) ? p : null;
        var node = new PresenceNode(deviceId, deviceName, location, mode, activity,
            Available: true, capabilities, now);
        _nodes[deviceId] = node;
        return node;
    }

    /// <summary>
    /// Removes a device's presence explicitly (disconnect/revocation). The Core
    /// continues — only the organ is gone (persistent self, replaceable organs).
    /// </summary>
    public bool Remove(string deviceId) =>
        !string.IsNullOrWhiteSpace(deviceId) && _nodes.TryRemove(deviceId, out _);

    /// <summary>All nodes, with Availability computed against the timeout (PresenceLost → false).</summary>
    public IReadOnlyList<PresenceNode> Current(DateTimeOffset now) =>
        _nodes.Values
            .Select(n => n with { Available = now - n.LastSeen < AvailabilityTimeout })
            .OrderBy(n => n.DeviceId)
            .ToList();

    /// <summary>
    /// Where is TOLIK needed? (docs/identity-presence.md §5 policy): picks the best
    /// available presence by the policy priorities — active conversation > interlocutor's
    /// device > task device > owner > family > quest > home > fallback. Pure routing:
    /// the answer NEVER becomes an authorization decision.
    /// </summary>
    public PresenceNode? Select(DateTimeOffset now,
        string? activeConversationDevice = null,
        string? interlocutorDevice = null,
        string? taskDevice = null,
        string? ownerDevice = null,
        string? familyDevice = null)
    {
        var available = Current(now).Where(n => n.Available).ToList();
        if (available.Count == 0) return null;

        foreach (var candidate in new[] { activeConversationDevice, interlocutorDevice, taskDevice, ownerDevice, familyDevice })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var match = available.FirstOrDefault(n =>
                string.Equals(n.DeviceId, candidate, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(n.DeviceName, candidate, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }

        // Fallback chain: Quest > home presence > first available.
        return available.FirstOrDefault(n => n.Capabilities.Contains("spatial"))
            ?? available.FirstOrDefault(n => n.Capabilities.Contains("home"))
            ?? available[0];
    }
}
