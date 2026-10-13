namespace TOLIK.Core;

/// <summary>
/// Genesis seed (docs/mind.md MI-9 — PLAN REFORM PRG-5b): the МЕХАНИЗМ of the
/// personality/temperament/values seed — pre-Genesis the seed DATA can be loaded
/// (the ceremony fills it), but the mind reads it ONLY after the ACTIVATION —
/// which flips at the Genesis ceremony's touchpoint (§85) and NEVER before. The
/// seed carries the personality traits and the core values ONLY — structurally no
/// capability/permission fields (the seed INFORMS the expression/personality
/// layers; the anti-self-escalation is structural: no API mutates the
/// authorization). The seed data itself is validated (the bounded temperament,
/// the non-empty values) — the ceremony provides the real content.
/// </summary>
public enum SeedStatus
{
    NotSeeded,
    Seeded,
    Activated
}

public sealed record GenesisSeedData(
    string Temperament,          // e.g. "calm-technical-dry-humor" — the bounded trait string
    string[] CoreValues,         // the immutable core values (above the personality)
    DateTimeOffset SeededAt)
{
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Temperament) && Temperament.Length <= 256 &&
        CoreValues.Length is >= 1 and <= 12 &&
        CoreValues.All(v => !string.IsNullOrWhiteSpace(v) && v.Length <= 128);
}

/// <summary>
/// The Genesis seed runtime (TOLIK's ONE seed — the identity is a single entity).
/// Thread-safe; the activation flips ONLY via ActivateGenesis (the ceremony's
/// touchpoint — called by the owner-gated ceremony path, never automatically); the
/// mind reads the seed ONLY when activated (the pre-Genesis honest boundary).
/// </summary>
public sealed class GenesisSeedRuntime
{
    private readonly object _gate = new();
    private GenesisSeedData? _seed;
    private bool _activated;

    public SeedStatus Status
    {
        get
        {
            lock (_gate) { return _activated ? SeedStatus.Activated : _seed is not null ? SeedStatus.Seeded : SeedStatus.NotSeeded; }
        }
    }

    public GenesisSeedData? Snapshot()
    {
        lock (_gate) { return _activated ? _seed : null; } // the mind reads the seed ONLY when activated
    }

    /// <summary>Loads the seed data (the ceremony's touchpoint fills it). The data
    /// must be valid; the activation is SEPARATE (the ceremony's act, never automatic).</summary>
    public void Load(GenesisSeedData seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        if (!seed.IsValid) throw new ArgumentException("The seed data is invalid (the temperament + 1-12 core values).", nameof(seed));
        lock (_gate)
        {
            if (_activated) throw new InvalidOperationException("The seed is already activated — the identity is born; the re-seed is not allowed (mind.md §7 continuity).");
            _seed = seed;
        }
    }

    /// <summary>The Genesis ceremony's touchpoint: flips Seeded → Activated. Requires
    /// the seed loaded; NEVER callable automatically (the owner's ceremony path only).</summary>
    public void Activate()
    {
        lock (_gate)
        {
            if (_seed is null) throw new InvalidOperationException("The seed is not loaded — the activation requires the ceremony's seed.");
            _activated = true;
        }
    }
}
