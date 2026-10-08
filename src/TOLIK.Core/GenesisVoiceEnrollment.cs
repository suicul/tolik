namespace TOLIK.Core;

/// <summary>One accepted Genesis voice enrollment sample (docs/voice-identity.md §4, VI-3):
/// embedding only — raw audio is never stored. The endpoint persists the accumulated set
/// as ONE "genesis.voice.enrollment" memory item (JSON: {samples:[{phraseIndex, embedding,
/// speechMs}]}).</summary>
public sealed record GenesisVoiceSample(int PhraseIndex, IReadOnlyList<double> Embedding, int SpeechMs);

/// <summary>
/// Shared sample gates for the voice enrollment flows (docs/voice-identity.md §4/§5,
/// VI-3/VI-4): quality (VAD speech duration + embedding present) and same-speaker
/// consistency (pairwise cosine ≥ 0.30 — VI-0: enrollment-set pairwise p5). Pure static
/// so BOTH enrollment state machines reuse the same math and thresholds (no duplicated
/// gate logic, no parallel systems).
/// </summary>
public static class VoiceSampleGates
{
    /// <summary>Enrollment consistency bar (VI-0: pairwise p5 of the owner dataset) —
    /// every pair of accepted samples of one enrollment must stay at/above this cosine.</summary>
    public const double EnrollmentConsistency = 0.30;

    /// <summary>Household distinctness bar: a new profile must stay BELOW this cosine to
    /// EVERY existing profile (VI-0: the measured foreign voice 0.399 passes just under —
    /// the bar is correct). Reuses the existing soft-accept constant (0.45).</summary>
    public const double HouseholdDistinctness = SpeakerProfile.AcceptTier;

    /// <summary>Quality gate: the embedding must be extracted (non-empty) AND the VAD
    /// speech duration at/above the calibrated minimum (fail closed for enrollment).</summary>
    public static bool QualityOk(IReadOnlyList<double>? embedding, int speechMs) =>
        embedding is not null && embedding.Count > 0 && speechMs >= SpeakerProfile.MinAdaptiveQualitySpeechMs;

    /// <summary>Same-speaker consistency between two accepted samples (mixed-speaker guard).</summary>
    public static bool Consistent(IReadOnlyList<double> embedding, IReadOnlyList<double> acceptedSample, double threshold = EnrollmentConsistency) =>
        SpeakerVerifier.Cosine(embedding, acceptedSample) >= threshold;

    /// <summary>Household distinctness: the new embedding must stay BELOW the bar to each
    /// existing profile — anti-contamination + anti-impersonation (docs/voice-identity.md §3).</summary>
    public static bool Distinct(IReadOnlyList<double> embedding, IReadOnlyList<double> existingProfileMean, double threshold = HouseholdDistinctness) =>
        SpeakerVerifier.Cosine(embedding, existingProfileMean) < threshold;
}

/// <summary>
/// Genesis Owner Voice Enrollment state machine (docs/voice-identity.md §4, VI-3).
/// Pure, thread-safe, per-owner (one DI singleton instance drives the endpoints); no
/// storage inside — endpoints persist via the durable memory "genesis.voice.enrollment"
/// item. Quality gate per sample (VAD speechMs + embedding), same-speaker consistency
/// (cosine ≥ 0.30 to every accepted sample — mixed-speaker guard), ≥4 of 5 distinct
/// phrases complete the enrollment. A failed sample is simply not accumulated — the
/// phase repeats without losing the set (plan §4: "при провале: конкретная фаза
/// повторяется, не весь набор").
/// </summary>
public sealed class GenesisVoiceEnrollment
{
    /// <summary>≥4 accepted distinct phrases complete the enrollment (plan §4).</summary>
    public const int CompletionMinPhrases = 4;
    /// <summary>Max distinct phrases (the fixed prompt list enforces this structurally —
    /// any index ≥ Phrases.Count is "index_out_of_range").</summary>
    public const int MaxDistinctPhrases = 5;

    public enum EnrollmentState { NotStarted, Collecting, Completed }

    private readonly object _gate = new();
    private readonly Dictionary<int, GenesisVoiceSample> _accepted = new();
    private EnrollmentState _state = EnrollmentState.NotStarted;
    private DateTimeOffset _lastAcceptedAt;

    public EnrollmentState State { get { lock (_gate) return _state; } }

    public int AcceptedCount { get { lock (_gate) return _accepted.Count; } }

    /// <summary>≥4 accepted distinct phrases complete the enrollment (plan §4).</summary>
    public bool IsComplete { get { lock (_gate) return _accepted.Count >= CompletionMinPhrases; } }

    /// <summary>Embedding dimension of the accepted set so far (null when empty) — the
    /// endpoint fails a dimension-mismatched upload cleanly (400) instead of a mid-gate 500.</summary>
    public int? AcceptedEmbeddingDimension
    {
        get { lock (_gate) return _accepted.Count == 0 ? null : _accepted.Values.First().Embedding.Count; }
    }

    /// <summary>
    /// Adds one enrollment sample: bounds check → duplicate check → quality gate
    /// (speechMs ≥ MinAdaptiveQualitySpeechMs AND embedding non-empty) → consistency
    /// gate (cosine ≥ 0.30 to every already-accepted sample). Reasons: "accepted" |
    /// "quality_low" | "inconsistent_with_sample_{n}" (n = the phrase index of the first
    /// conflicting accepted sample) | "phrase_already_accepted" | "index_out_of_range".
    /// Post-completion samples are refused with "phrase_already_accepted" — the set is
    /// final until Reset.
    /// </summary>
    public (bool Accepted, string Reason) AddSample(int phraseIndex, IReadOnlyList<double> embedding, int speechMs, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (phraseIndex < 0 || phraseIndex >= GenesisVoicePhrases.Phrases.Count)
                return (false, "index_out_of_range");
            if (_state == EnrollmentState.Completed || _accepted.ContainsKey(phraseIndex))
                return (false, "phrase_already_accepted");
            if (!VoiceSampleGates.QualityOk(embedding, speechMs))
                return (false, "quality_low");
            foreach (var sample in _accepted.Values)
                if (!VoiceSampleGates.Consistent(embedding, sample.Embedding))
                    return (false, $"inconsistent_with_sample_{sample.PhraseIndex}");
            _accepted[phraseIndex] = new GenesisVoiceSample(phraseIndex, embedding.ToArray(), speechMs);
            _lastAcceptedAt = now;
            _state = EnrollmentState.Collecting;
            return (true, "accepted");
        }
    }

    /// <summary>
    /// Builds the canonical v2 profile from the accepted embeddings (≥4) — deterministic,
    /// no storage inside. Null when incomplete. Marks the machine Completed (the set is
    /// final); the endpoint resets after persisting so a future re-enrollment can start.
    /// </summary>
    public SpeakerProfile? BuildProfile()
    {
        lock (_gate)
        {
            if (_accepted.Count < CompletionMinPhrases) return null;
            var canonical = _accepted.Keys.OrderBy(i => i)
                .Select(i => (IReadOnlyList<double>)_accepted[i].Embedding).ToList();
            _state = EnrollmentState.Completed;
            return new SpeakerProfile(canonical, null, "campplus", _lastAcceptedAt);
        }
    }

    /// <summary>Snapshot of the accepted samples in phrase order — the endpoint persists
    /// this as ONE "genesis.voice.enrollment" memory item (delete+add pattern).</summary>
    public IReadOnlyList<GenesisVoiceSample> Snapshot()
    {
        lock (_gate)
            return _accepted.Keys.OrderBy(i => i).Select(i => _accepted[i]).ToList();
    }

    /// <summary>Clears the set (a repeated ceremony attempt / owner retry of the whole
    /// flow, and the post-completion cleanup).</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _accepted.Clear();
            _state = EnrollmentState.NotStarted;
            _lastAcceptedAt = default;
        }
    }
}
