using TOLIK.Core;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// Genesis Owner Voice Enrollment state machine (docs/voice-identity.md §4, VI-3):
/// quality gate per sample (VAD speechMs + embedding), same-speaker consistency
/// (cosine ≥ 0.30 — mixed-speaker guard), ≥4 of 5 distinct phrases complete, reset
/// clears for an owner retry; the fixed phrase list is 5 distinct non-empty prompts.
/// Thresholds are VI-0-calibrated constants asserted here so silent drift fails tests.
/// </summary>
public sealed class GenesisVoiceEnrollmentTests
{
    // Unit vectors, pairwise cosine ≥ 0.30 (same-speaker-like): the consistent distinct set.
    private static readonly IReadOnlyList<double>[] DistinctSet =
    [
        [1.0, 0.0, 0.0, 0.0],
        [0.6, 0.8, 0.0, 0.0],
        [0.6, 0.64, 0.48, 0.0],
        [0.6, 0.64, 0.0, 0.48],
    ];

    // Different-speaker-like: cosine 0 to DistinctSet[0] — the mixed-set intruder.
    private static readonly IReadOnlyList<double> ForeignLike = [0.0, 1.0, 0.0, 0.0];

    [Fact]
    public void Phrase_list_has_five_distinct_nonempty_prompts()
    {
        Assert.Equal(5, GenesisVoicePhrases.Phrases.Count);
        Assert.All(GenesisVoicePhrases.Phrases, p => Assert.False(string.IsNullOrWhiteSpace(p)));
        Assert.Equal(5, GenesisVoicePhrases.Phrases.Distinct().Count());
        // Phonetic coverage across the set: sibilants + the р/л contrast (docs §4).
        Assert.Contains(GenesisVoicePhrases.Phrases, p => p.Contains('ш') || p.Contains('щ') || p.Contains('ж') || p.Contains('ч'));
        Assert.Contains(GenesisVoicePhrases.Phrases, p => p.Contains('р') && p.Contains('л'));
    }

    [Fact]
    public void Phrase_bounds_check()
    {
        Assert.Equal(GenesisVoicePhrases.Phrases[0], GenesisVoicePhrases.Phrase(0));
        Assert.Equal(GenesisVoicePhrases.Phrases[4], GenesisVoicePhrases.Phrase(4));
        Assert.Throws<ArgumentOutOfRangeException>(() => GenesisVoicePhrases.Phrase(5));
        Assert.Throws<ArgumentOutOfRangeException>(() => GenesisVoicePhrases.Phrase(-1));
    }

    [Fact]
    public void Quality_gate_rejects_short_or_empty_samples()
    {
        var enrollment = new GenesisVoiceEnrollment();
        // 2499ms: one ms below the VI-0 quality bar — refused.
        var (accepted2499, reason2499) = enrollment.AddSample(0, DistinctSet[0], 2499, DateTimeOffset.UtcNow);
        Assert.False(accepted2499);
        Assert.Equal("quality_low", reason2499);
        // Empty embedding (service returned nothing usable) — refused.
        var (acceptedEmpty, reasonEmpty) = enrollment.AddSample(0, [], 5000, DateTimeOffset.UtcNow);
        Assert.False(acceptedEmpty);
        Assert.Equal("quality_low", reasonEmpty);
        Assert.Equal(0, enrollment.AcceptedCount);
    }

    [Fact]
    public void Consistency_gate_rejects_mixed_speaker_set()
    {
        var enrollment = new GenesisVoiceEnrollment();
        Assert.True(enrollment.AddSample(0, DistinctSet[0], 3000, DateTimeOffset.UtcNow).Accepted);
        // A different-speaker vector (cosine 0 < 0.30) — refused, named by its conflicting sample.
        var (accepted, reason) = enrollment.AddSample(1, ForeignLike, 3000, DateTimeOffset.UtcNow);
        Assert.False(accepted);
        Assert.Equal("inconsistent_with_sample_0", reason);
        Assert.Equal(1, enrollment.AcceptedCount); // the failed sample is not accumulated
    }

    [Fact]
    public void Distinct_set_is_accepted_and_completes_at_four()
    {
        var enrollment = new GenesisVoiceEnrollment();
        for (var i = 0; i < 3; i++)
            Assert.True(enrollment.AddSample(i, DistinctSet[i], 3000, DateTimeOffset.UtcNow).Accepted);
        Assert.False(enrollment.IsComplete); // 3 of 5 — not yet (plan §4: ≥4)
        Assert.True(enrollment.AddSample(3, DistinctSet[3], 3000, DateTimeOffset.UtcNow).Accepted);
        Assert.True(enrollment.IsComplete);
        Assert.Equal(4, enrollment.AcceptedCount);
    }

    [Fact]
    public void BuildProfile_is_null_when_incomplete()
    {
        var enrollment = new GenesisVoiceEnrollment();
        Assert.Null(enrollment.BuildProfile());
        for (var i = 0; i < 3; i++)
            enrollment.AddSample(i, DistinctSet[i], 3000, DateTimeOffset.UtcNow);
        Assert.Null(enrollment.BuildProfile()); // 3 < 4
    }

    [Fact]
    public void BuildProfile_returns_canonical_set_when_complete()
    {
        var enrollment = new GenesisVoiceEnrollment();
        for (var i = 0; i < 4; i++)
            enrollment.AddSample(i, DistinctSet[i], 3000, DateTimeOffset.UtcNow);
        var profile = enrollment.BuildProfile();
        Assert.NotNull(profile);
        Assert.Equal(4, profile!.Canonical.Count);
        // The canonical mean recognizes a same-speaker vector (owner test #1 shape).
        Assert.True(SpeakerVerifier.Accept(profile.Similarity(DistinctSet[0])));
    }

    [Fact]
    public void Reset_clears_for_a_retry()
    {
        var enrollment = new GenesisVoiceEnrollment();
        for (var i = 0; i < 4; i++)
            enrollment.AddSample(i, DistinctSet[i], 3000, DateTimeOffset.UtcNow);
        Assert.True(enrollment.IsComplete);
        Assert.NotNull(enrollment.BuildProfile()); // set finalized
        enrollment.Reset();
        Assert.Equal(0, enrollment.AcceptedCount);
        Assert.False(enrollment.IsComplete);
        Assert.Null(enrollment.BuildProfile());
        // A repeated ceremony attempt / owner retry of the whole set works after Reset.
        Assert.True(enrollment.AddSample(0, DistinctSet[0], 3000, DateTimeOffset.UtcNow).Accepted);
    }

    [Fact]
    public void Duplicate_phrase_and_out_of_range_are_refused()
    {
        var enrollment = new GenesisVoiceEnrollment();
        Assert.True(enrollment.AddSample(0, DistinctSet[0], 3000, DateTimeOffset.UtcNow).Accepted);
        var (duplicate, duplicateReason) = enrollment.AddSample(0, DistinctSet[0], 3000, DateTimeOffset.UtcNow);
        Assert.False(duplicate);
        Assert.Equal("phrase_already_accepted", duplicateReason);
        var (outOfRange, outOfRangeReason) = enrollment.AddSample(5, DistinctSet[0], 3000, DateTimeOffset.UtcNow);
        Assert.False(outOfRange);
        Assert.Equal("index_out_of_range", outOfRangeReason);
    }

    [Fact]
    public void Calibrated_gates_are_declared_constants()
    {
        // VI-0 (docs/voice-identity.md §3/§4): any silent drift of the calibrated numbers fails here.
        Assert.Equal(0.30, VoiceSampleGates.EnrollmentConsistency);
        Assert.Equal(SpeakerProfile.AcceptTier, VoiceSampleGates.HouseholdDistinctness);
        Assert.Equal(2500, SpeakerProfile.MinAdaptiveQualitySpeechMs);
        Assert.Equal(4, GenesisVoiceEnrollment.CompletionMinPhrases);
        Assert.Equal(5, GenesisVoiceEnrollment.MaxDistinctPhrases);
    }
}
