using TOLIK.Core;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// Conversational trusted-person enrollment state machine (docs/voice-identity.md §5-§6,
/// VI-4): the "Owner" role is rejected explicitly (the target never becomes Owner — owner
/// req #16); the unknown-caller invariant (an enrollment session can ONLY be opened by a
/// verified Owner turn — enforced by the /voice/person/enroll endpoint gate) is represented
/// here by the "no_active_enrollment" path: without an Owner-started session even sample
/// uploads are refused. Consistency (≥0.30) + household distinctness (<0.45 —
/// "too_similar_to_existing_profile") gates; ≥2 samples complete; TTL 5 min expiry.
/// </summary>
public sealed class TrustedPersonEnrollmentTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    // Target-speaker-like vectors: pairwise cosine ≥ 0.30 (consistent), distinct from
    // the household set below.
    private static readonly IReadOnlyList<double> TargetA = [1.0, 0.0, 0.0, 0.0];
    private static readonly IReadOnlyList<double> TargetB = [0.6, 0.8, 0.0, 0.0];

    // Existing household profiles passed in by the caller (durable memory): the owner's
    // own profile (cos 0 to both target vectors — distinctness passes) and a colliding
    // profile (cos ≥ 0.45 to the target — distinctness must refuse).
    private static readonly IReadOnlyList<double> OwnerProfile = [0.0, 0.0, 0.0, 1.0];
    private static readonly IReadOnlyList<double> CollidingProfile = [0.71, 0.71, 0.0, 0.0];

    // Mixed-set intruder: cosine 0 to TargetA (consistency must refuse); distinct from
    // OwnerProfile — so the consistency gate refusing it FIRST is the gate-order check.
    private static readonly IReadOnlyList<double> ForeignLike = [0.0, 0.0, 1.0, 0.0];

    [Fact]
    public void Owner_role_is_rejected_explicitly()
    {
        var enrollment = new TrustedPersonEnrollment();
        // NEVER "Owner" (owner req #16; docs/SECURITY.md §18: enrollment is explicit,
        // role-scoped — bootstrap is the only Owner path). Unknown roles fail closed too.
        Assert.Throws<ArgumentException>(() => enrollment.Start(Guid.NewGuid(), "Мария", "Owner", Now));
        Assert.Throws<ArgumentException>(() => enrollment.Start(Guid.NewGuid(), "Мария", "AuthorizedUser", Now));
        Assert.Throws<ArgumentException>(() => enrollment.Start(Guid.NewGuid(), "Мария", "", Now));
        Assert.Equal(0, enrollment.AcceptedCount(Guid.NewGuid()));
    }

    [Fact]
    public void Unknown_caller_cannot_open_or_feed_a_session()
    {
        // Owner-authorization invariant (docs/voice-identity.md §5): the enrollment session
        // can ONLY be started by a verified Owner turn — enforced by the /voice/person/enroll
        // endpoint gate (Owner role + speaker tier High; an unknown voice NEVER enrolls,
        // owner req #17). The unit-representable form: without a Start (which only the
        // Owner-gated endpoint performs) even sample uploads are refused — an unknown
        // speaker cannot open one, and no sample can be injected into a session that
        // was never opened.
        var enrollment = new TrustedPersonEnrollment();
        var (accepted, reason) = enrollment.AddSample(Guid.NewGuid(), TargetA, 3000, Now, [OwnerProfile]);
        Assert.False(accepted);
        Assert.Equal("no_active_enrollment", reason);
        Assert.False(enrollment.IsComplete(Guid.NewGuid()));
        Assert.Null(enrollment.BuildProfile(Guid.NewGuid()));
    }

    [Fact]
    public void Quality_gate_rejects_short_samples()
    {
        var enrollment = new TrustedPersonEnrollment();
        var owner = Guid.NewGuid();
        enrollment.Start(owner, "Мария", "Family", Now);
        // 2499ms: one ms below the VI-0 quality bar — refused.
        var (accepted, reason) = enrollment.AddSample(owner, TargetA, 2499, Now, [OwnerProfile]);
        Assert.False(accepted);
        Assert.Equal("quality_low", reason);
        Assert.Equal(0, enrollment.AcceptedCount(owner));
    }

    [Fact]
    public void Consistency_gate_rejects_mixed_speaker_set()
    {
        var enrollment = new TrustedPersonEnrollment();
        var owner = Guid.NewGuid();
        enrollment.Start(owner, "Мария", "Family", Now);
        Assert.True(enrollment.AddSample(owner, TargetA, 3000, Now, [OwnerProfile]).Accepted);
        // A different-speaker vector (cosine 0 < 0.30) — consistency refuses BEFORE the
        // distinctness gate (this vector also collides with OwnerProfile: gate order matters).
        var (accepted, reason) = enrollment.AddSample(owner, ForeignLike, 3000, Now, [OwnerProfile]);
        Assert.False(accepted);
        Assert.Equal("inconsistent_with_sample_0", reason);
        Assert.Equal(1, enrollment.AcceptedCount(owner));
    }

    [Fact]
    public void Distinctness_gate_rejects_profile_collisions()
    {
        var enrollment = new TrustedPersonEnrollment();
        var owner = Guid.NewGuid();
        enrollment.Start(owner, "Мария", "Family", Now);
        // A voice colliding with an existing household profile (cos ≥ 0.45) — refused:
        // anti-contamination + anti-impersonation (docs/voice-identity.md §3).
        var (accepted, reason) = enrollment.AddSample(owner, TargetA, 3000, Now, [CollidingProfile]);
        Assert.False(accepted);
        Assert.Equal("too_similar_to_existing_profile", reason);
        // Below the 0.45 bar the same vector passes the distinctness gate (owner's profile).
        var (accepted2, reason2) = enrollment.AddSample(owner, TargetA, 3000, Now, [OwnerProfile]);
        Assert.True(accepted2);
        Assert.Equal("accepted", reason2);
    }

    [Fact]
    public void IsComplete_needs_two_samples_and_builds_profile()
    {
        var enrollment = new TrustedPersonEnrollment();
        var owner = Guid.NewGuid();
        enrollment.Start(owner, "Мария", "Family", Now);
        Assert.False(enrollment.IsComplete(owner));
        Assert.Null(enrollment.BuildProfile(owner));
        Assert.True(enrollment.AddSample(owner, TargetA, 3000, Now, [OwnerProfile]).Accepted);
        Assert.False(enrollment.IsComplete(owner)); // 1 of 2–5 — not yet
        Assert.True(enrollment.AddSample(owner, TargetB, 3000, Now, [OwnerProfile]).Accepted);
        Assert.True(enrollment.IsComplete(owner));
        var profile = enrollment.BuildProfile(owner);
        Assert.NotNull(profile);
        Assert.Equal(2, profile!.Canonical.Count);
        // The canonical mean recognizes a same-speaker vector (owner test #2 shape).
        Assert.True(SpeakerVerifier.Accept(profile.Similarity(TargetA)));
    }

    [Fact]
    public void Max_samples_bound_is_five()
    {
        var enrollment = new TrustedPersonEnrollment();
        var owner = Guid.NewGuid();
        enrollment.Start(owner, "Мария", "Family", Now);
        // Same target vector five times: consistent (1.0) and distinct from OwnerProfile (0).
        for (var i = 0; i < TrustedPersonEnrollment.MaxSamples; i++)
            Assert.True(enrollment.AddSample(owner, TargetA, 3000, Now, [OwnerProfile]).Accepted);
        var (accepted, reason) = enrollment.AddSample(owner, TargetA, 3000, Now, [OwnerProfile]);
        Assert.False(accepted);
        Assert.Equal("max_samples_reached", reason);
    }

    [Fact]
    public void Ttl_expiry_smoke()
    {
        var enrollment = new TrustedPersonEnrollment();
        var owner = Guid.NewGuid();
        var context = enrollment.Start(owner, "Мария", "Family", Now);
        Assert.Equal(TrustedPersonEnrollment.EntryTtl, context.ExpiresAt - context.StartedAt);
        Assert.True(enrollment.AddSample(owner, TargetA, 3000, Now).Accepted);
        // After the 5-minute TTL the entry is gone — the caller must Start again.
        var (accepted, reason) = enrollment.AddSample(owner, TargetB, 3000, Now.AddMinutes(6), [OwnerProfile]);
        Assert.False(accepted);
        Assert.Equal("session_expired", reason);
        Assert.Equal(0, enrollment.AcceptedCount(owner));
        Assert.False(enrollment.IsComplete(owner));
        // A fresh Start works (retry).
        enrollment.Start(owner, "Мария", "Family", Now.AddMinutes(6));
        Assert.True(enrollment.AddSample(owner, TargetA, 3000, Now.AddMinutes(7)).Accepted);
    }
}
