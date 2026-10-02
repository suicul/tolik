using System.Collections.Concurrent;

namespace TOLIK.Infrastructure.Models;

public enum ModelRoutingPreference
{
    Cloud,
    Local
}

/// <summary>
/// Remembered per-user model-routing preference (docs/ROADMAP.md model policy,
/// ask-policy item): when the owner answers the cloud-vs-local ask for coding, the
/// choice is remembered and applied to future coding-route resolution. In-memory
/// per-process (restart clears it; the ask is re-presented — durable table is
/// future work, mirroring the ConfirmationStore precedent).
/// </summary>
public sealed class ModelPreferenceStore
{
    private readonly ConcurrentDictionary<Guid, ModelRoutingPreference> _preferences = new();

    /// <summary>Sets (idempotently overwrites) the routing preference for a user.</summary>
    public void Set(Guid userId, ModelRoutingPreference preference)
    {
        if (userId == Guid.Empty) throw new ArgumentException("User is required.", nameof(userId));
        _preferences[userId] = preference;
    }

    /// <summary>Returns the remembered preference, or null when the ask has not been answered yet.</summary>
    public ModelRoutingPreference? Get(Guid userId) =>
        _preferences.TryGetValue(userId, out var preference) ? preference : null;

    /// <summary>Clears the preference: the router returns to its default (ask again).</summary>
    public void Clear(Guid userId) => _preferences.TryRemove(userId, out _);
}
