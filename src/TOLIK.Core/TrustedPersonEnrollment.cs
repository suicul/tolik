namespace TOLIK.Core;

/// <summary>Context of an active trusted-person enrollment (docs/voice-identity.md §5,
/// VI-4): keyed by the initiating owner, TTL-bounded. The target NEVER receives the
/// Owner role (Start rejects it explicitly) — voice recognition is a hint only (§13).</summary>
public sealed record TrustedPersonEnrollmentContext(
    Guid OwnerUserId, string TargetName, string Role, DateTimeOffset StartedAt, DateTimeOffset ExpiresAt);

/// <summary>One accepted trusted-person sample — embedding only, raw audio never stored.</summary>
public sealed record TrustedPersonVoiceSample(IReadOnlyList<double> Embedding, int SpeechMs);

/// <summary>
/// Conversational trusted-person enrollment (docs/voice-identity.md §5-§6, VI-4):
/// an ACTIVE-enrollment map keyed by the initiating-owner userId, TTL 5 min per entry.
/// OWNER-AUTHORIZATION INVARIANT: an enrollment session can ONLY be opened by a verified
/// Owner turn (Role == Owner AND speaker tier High) — enforced by the /voice/person/enroll
/// endpoint (docs/voice-identity.md §5: an unknown speaker cannot even open one); the
/// state machine itself has no other entry path. The target NEVER receives the Owner
/// role (Start rejects it explicitly — owner req #16) and never gains permissions beyond
/// the role — permissions come from the role, never from the voice (§13/§7).
/// Gates per sample: quality (VAD speechMs + embedding) + same-speaker consistency
/// (cosine ≥ 0.30 to every accepted sample) + household distinctness (cosine < 0.45
/// to EVERY existing profile passed in by the caller from durable memory — a colliding
/// voice is refused: "too_similar_to_existing_profile"; anti-contamination +
/// anti-impersonation). Pure, thread-safe; no storage inside — the endpoint persists
/// via DurableMemoryStore.
/// </summary>
public sealed class TrustedPersonEnrollment
{
    /// <summary>≥2 accepted samples complete the enrollment (plan §5: 2–5 phrases).</summary>
    public const int MinSamples = 2;
    public const int MaxSamples = 5;
    /// <summary>TTL per active entry (plan §5): an expired entry is dropped — the caller
    /// must Start again.</summary>
    public static readonly TimeSpan EntryTtl = TimeSpan.FromMinutes(5);

    private readonly object _gate = new();
    private readonly Dictionary<Guid, ActiveEnrollment> _active = new();

    private sealed class ActiveEnrollment(TrustedPersonEnrollmentContext context)
    {
        public TrustedPersonEnrollmentContext Context = context;
        public readonly List<TrustedPersonVoiceSample> Samples = [];
        public DateTimeOffset LastAcceptedAt;
        // Delegates to the context: the continuation (with { Role = ... }) preserves it.
        public DateTimeOffset ExpiresAt => Context.ExpiresAt;
    }

    /// <summary>The active enrollment context (target/role/expiry), or null when none/expired —
    /// the continuation surface for multi-request flows.</summary>
    public TrustedPersonEnrollmentContext? GetContext(Guid ownerUserId, DateTimeOffset now)
    {
        lock (_gate)
            return _active.TryGetValue(ownerUserId, out var entry) && entry.ExpiresAt > now ? entry.Context : null;
    }

    /// <summary>
    /// Opens (or continues) an enrollment for the target: role MUST be "Family" or
    /// "TrustedGuest" — anything else (including "Owner") is rejected explicitly (owner
    /// req #16: the target never becomes Owner; bootstrap is the only Owner path).
    /// An active, unexpired entry for the SAME target continues (accumulated samples
    /// survive); a different target replaces it.
    /// </summary>
    public TrustedPersonEnrollmentContext Start(Guid ownerUserId, string targetName, string role, DateTimeOffset now)
    {
        if (ownerUserId == Guid.Empty)
            throw new ArgumentException("Owner user id is required.", nameof(ownerUserId));
        if (string.IsNullOrWhiteSpace(targetName) || targetName.Length > 128)
            throw new ArgumentException("Target name is required (max 128 chars).", nameof(targetName));
        // Raw role strings are never trusted (TolikRoles philosophy): unknown, empty or
        // case-variant names fail closed — the target NEVER becomes Owner.
        var normalizedRole = role?.Trim() switch
        {
            "Family" => "Family",
            "TrustedGuest" => "TrustedGuest",
            _ => throw new ArgumentException(
                $"Role must be \"Family\" or \"TrustedGuest\" — \"{role}\" is rejected (the target never becomes Owner).", nameof(role)),
        };
        lock (_gate)
        {
            if (_active.TryGetValue(ownerUserId, out var existing) && existing.ExpiresAt > now &&
                string.Equals(existing.Context.TargetName, targetName.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                // Same target, still active — continue; the latest requested role applies.
                existing.Context = existing.Context with { Role = normalizedRole };
                return existing.Context;
            }
            var context = new TrustedPersonEnrollmentContext(ownerUserId, targetName.Trim(), normalizedRole, now, now.Add(EntryTtl));
            _active[ownerUserId] = new ActiveEnrollment(context);
            return context;
        }
    }

    /// <summary>
    /// Adds one sample to the owner's active enrollment: quality gate (speechMs +
    /// embedding) → same-speaker consistency (cosine ≥ 0.30 to every accepted sample) →
    /// household distinctness (cosine < 0.45 to EVERY existing profile passed in by the
    /// caller). Reasons: "accepted" | "quality_low" | "inconsistent_with_sample_{n}"
    /// (n = the ordinal position of the first conflicting sample) |
    /// "too_similar_to_existing_profile" | "max_samples_reached" | "session_expired" |
    /// "no_active_enrollment". An expired entry is dropped (TTL) — the caller must Start again.
    /// </summary>
    public (bool Accepted, string Reason) AddSample(
        Guid ownerUserId,
        IReadOnlyList<double> embedding,
        int speechMs,
        DateTimeOffset now,
        IReadOnlyList<IReadOnlyList<double>>? existingHouseholdVectors = null)
    {
        lock (_gate)
        {
            if (!_active.TryGetValue(ownerUserId, out var entry))
                return (false, "no_active_enrollment");
            if (entry.ExpiresAt <= now)
            {
                _active.Remove(ownerUserId);
                return (false, "session_expired");
            }
            if (entry.Samples.Count >= MaxSamples)
                return (false, "max_samples_reached");
            if (!VoiceSampleGates.QualityOk(embedding, speechMs))
                return (false, "quality_low");
            for (var i = 0; i < entry.Samples.Count; i++)
                if (!VoiceSampleGates.Consistent(embedding, entry.Samples[i].Embedding))
                    return (false, $"inconsistent_with_sample_{i}");
            if (existingHouseholdVectors is not null)
                foreach (var existingMean in existingHouseholdVectors)
                    if (!VoiceSampleGates.Distinct(embedding, existingMean))
                        return (false, "too_similar_to_existing_profile");
            entry.Samples.Add(new TrustedPersonVoiceSample(embedding.ToArray(), speechMs));
            entry.LastAcceptedAt = now;
            return (true, "accepted");
        }
    }

    /// <summary>Accepted sample count of the active entry (0 when none/expired).</summary>
    public int AcceptedCount(Guid ownerUserId)
    {
        lock (_gate)
            return _active.TryGetValue(ownerUserId, out var entry) && entry.ExpiresAt > DateTimeOffset.UtcNow
                ? entry.Samples.Count
                : 0;
    }

    /// <summary>≥2 accepted samples complete the enrollment (plan §5: 2–5 phrases).</summary>
    public bool IsComplete(Guid ownerUserId)
    {
        lock (_gate)
            return _active.TryGetValue(ownerUserId, out var entry)
                && entry.ExpiresAt > DateTimeOffset.UtcNow
                && entry.Samples.Count >= MinSamples;
    }

    /// <summary>Builds the canonical profile from the accepted samples (≥2). Null when
    /// incomplete.</summary>
    public SpeakerProfile? BuildProfile(Guid ownerUserId)
    {
        lock (_gate)
        {
            if (!_active.TryGetValue(ownerUserId, out var entry) || entry.Samples.Count < MinSamples)
                return null;
            var canonical = entry.Samples.Select(s => (IReadOnlyList<double>)s.Embedding).ToList();
            return new SpeakerProfile(canonical, null, "campplus",
                entry.LastAcceptedAt == default ? entry.Context.StartedAt : entry.LastAcceptedAt);
        }
    }

    /// <summary>Closes the active enrollment (post-completion cleanup / explicit retry).</summary>
    public void Remove(Guid ownerUserId)
    {
        lock (_gate) _active.Remove(ownerUserId);
    }
}
