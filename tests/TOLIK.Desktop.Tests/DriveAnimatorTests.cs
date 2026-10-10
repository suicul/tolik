using TOLIK.Desktop.Bridge;
using TOLIK.Desktop.Motion;

namespace TOLIK.Desktop.Tests;

/// <summary>
/// The animator under a live Core drive (CE-2): events set TARGETS — the body
/// eases toward them (nothing snaps), procedural micro-life composes on top
/// (blinks, saccades, pulses, ambient wobble stay alive), and ClearDrive
/// returns to the honest Phase 1 procedural idle. Core's mode drives the tone:
/// red exists ONLY when Core declared it (Alert/Locked), never by renderer guess.
/// </summary>
public class DriveAnimatorTests
{
    private static CephalonAnimator NewAnimator() =>
        new(IdleMotionConfig.Default, new XorShiftRandom(0x5EED));

    private static ExpressionDrive Drive(
        CephalonState state = CephalonState.Idle,
        double eyeOpen = 0.75,
        double pupilScale = 1.0,
        double glow = 0.25,
        double particles = 0.1,
        double shellTilt = 0.0,
        double expansionCenter = 1.0,
        double wobble = 0.0,
        double drift = 1.0,
        double spectrum = 0.0,
        double movement = 0.15,
        double transition = 0.3,
        double anchorX = 0.0,
        double anchorY = 0.0) => new(
            state, eyeOpen, pupilScale, shellTilt, expansionCenter, wobble,
            glow, particles, drift, 0.08, spectrum, anchorX, anchorY, movement, transition);

    private static void RunFor(CephalonAnimator animator, double seconds, Action<double>? sample = null)
    {
        const double dt = 1.0 / 60.0;
        int steps = (int)(seconds / dt);
        for (int i = 0; i < steps; i++)
        {
            animator.Update(dt);
            sample?.Invoke(animator.Parameters.EyeOpen);
        }
    }

    // ---------- drive adoption ----------

    [Fact]
    public void ApplyDrive_owns_state_and_tone_from_core_modes()
    {
        CephalonAnimator animator = NewAnimator();

        animator.ApplyDrive(Drive(CephalonState.Speaking));
        Assert.True(animator.HasDrive);
        Assert.Equal(CephalonState.Speaking, animator.State);
        Assert.Equal(CephalonTone.Voice, animator.Traits.Tone);

        animator.ApplyDrive(Drive(CephalonState.Alert));
        Assert.Equal(CephalonState.Alert, animator.State);
        Assert.Equal(CephalonTone.Error, animator.Traits.Tone); // red — from a REAL Core event

        animator.ApplyDrive(Drive(CephalonState.Working));
        Assert.Equal(CephalonTone.Working, animator.Traits.Tone);

        animator.ApplyDrive(Drive(CephalonState.Recovering));
        Assert.Equal(CephalonTone.Calm, animator.Traits.Tone); // healing, never red

        animator.ApplyDrive(Drive(CephalonState.Locked));
        Assert.Equal(CephalonTone.Locked, animator.Traits.Tone); // fail-closed rendered visually
    }

    [Fact]
    public void Core_eyeOpenness_owns_the_eyelid_and_nothing_snaps()
    {
        CephalonAnimator animator = NewAnimator();
        RunFor(animator, 6.0); // settle idle: eye fully open (~1.0)

        animator.ApplyDrive(Drive(eyeOpen: 0.25));

        // One 16 ms step: eased progress toward 0.25 — a fraction of the distance, never a snap.
        double before = animator.Parameters.EyeOpen;
        animator.Update(1.0 / 60.0);
        double after = animator.Parameters.EyeOpen;
        Assert.True(after < before, "eyelid must start closing immediately");
        Assert.True(before - after < 0.2, $"snap detected: jumped {before - after:0.00}");
        Assert.True(before - after > 0.02, "easing must actually move in one step");

        // Settled (the descent from 1.0 is easing, not a snap): the lid then lives
        // AT Core's target — blink micro-life only ever dips BELOW it.
        RunFor(animator, 2.0);
        var samples = new List<double>();
        RunFor(animator, 6.0, samples.Add);
        Assert.All(samples, s => Assert.True(s <= 0.26, $"lid rose above target: {s:0.00}"));
        Assert.Contains(samples, s => s >= 0.24); // it actually reached the target
    }

    [Fact]
    public void Core_glow_and_particles_own_their_targets()
    {
        CephalonAnimator animator = NewAnimator();

        animator.ApplyDrive(Drive(glow: 0.9, particles: 0.85));
        RunFor(animator, 4.0);

        // Pulses only ADD energy on top of the baseline; easing approaches from below.
        Assert.InRange(animator.Parameters.GlowIntensity, 0.80, 1.0);
        Assert.InRange(animator.Parameters.ParticleEnergy, 0.64, 1.0);
    }

    [Fact]
    public void Core_eyeScale_is_tracked_continuously_not_only_on_saccades()
    {
        CephalonAnimator animator = NewAnimator();
        RunFor(animator, 2.0);

        // Core widens focus; the body must follow within ~1 s — long before any
        // saccade retarget (saccade interval starts at 2 s).
        animator.ApplyDrive(Drive(pupilScale: 1.3));
        RunFor(animator, 1.0);

        Assert.InRange(animator.Parameters.PupilScale, 1.15, CephalonParameters.PupilScaleMax);
    }

    [Fact]
    public void Core_rotation_owns_the_drift_and_procedural_retarget_stands_down()
    {
        CephalonAnimator animator = NewAnimator();

        animator.ApplyDrive(Drive(drift: 1.5));
        RunFor(animator, 6.0); // several procedural drift retargets would fire here

        Assert.InRange(animator.Parameters.DriftMultiplier, 1.40, 1.5);
    }

    [Fact]
    public void Core_shell_fields_own_tilt_center_and_deformation()
    {
        CephalonAnimator animator = NewAnimator();

        animator.ApplyDrive(Drive(
            shellTilt: CephalonParameters.ShellTiltMax,
            expansionCenter: CephalonParameters.ShellExpansionMax));
        RunFor(animator, 5.0);

        // Tilt: Core owns the form — the procedural sway stands down.
        Assert.InRange(animator.Parameters.ShellTilt,
            CephalonParameters.ShellTiltMax - 0.005, CephalonParameters.ShellTiltMax);
        // Expansion: Core owns the CENTER — breathing continues AROUND it.
        Assert.InRange(animator.Parameters.ShellExpansion,
            CephalonParameters.ShellExpansionMax - 0.02, CephalonParameters.ShellExpansionMax);
    }

    [Fact]
    public void Core_shellDeformation_adds_to_the_ambient_wobble_and_zero_keeps_it_alive()
    {
        // Zero request: the ambient wobble (the renderer's own generator) survives.
        // The first procedural wobble retarget is drawn from a 6–14 s interval,
        // so the window must cover the first retarget.
        CephalonAnimator calm = NewAnimator();
        calm.ApplyDrive(Drive(wobble: 0.0));
        double calmMax = 0.0;
        RunFor(calm, 16.0, _ => calmMax = Math.Max(calmMax, calm.Parameters.WobbleAmp));
        Assert.True(calmMax > 0.004, $"ambient wobble died under a zero drive: max {calmMax:0.000}");

        // Full request: the shell visibly deforms, bounded by the parameter max.
        CephalonAnimator loud = NewAnimator();
        loud.ApplyDrive(Drive(wobble: CephalonParameters.WobbleMax));
        double loudMax = 0.0;
        RunFor(loud, 10.0, _ => loudMax = Math.Max(loudMax, loud.Parameters.WobbleAmp));
        Assert.InRange(loudMax, CephalonParameters.WobbleMax * 0.5, CephalonParameters.WobbleMax);
    }

    [Fact]
    public void Spectrum_is_carried_truthfully_and_adds_exactly_zero_at_zero()
    {
        CephalonAnimator animator = NewAnimator();

        animator.ApplyDrive(Drive(spectrum: 0.0));
        RunFor(animator, 2.0);
        Assert.Equal(0.0, animator.Parameters.SpectrumIntensity); // exactly zero, always

        animator.ApplyDrive(Drive(spectrum: 0.6));
        RunFor(animator, 2.0);
        Assert.InRange(animator.Parameters.SpectrumIntensity, 0.5, 0.7); // a REAL axis

        animator.ClearDrive();
        RunFor(animator, 1.0);
        Assert.True(animator.Parameters.SpectrumIntensity < 0.1,
            "without a drive the spectrum must fall back to honest silence");
    }

    [Fact]
    public void Attention_anchor_steers_the_saccade_wander()
    {
        CephalonAnimator animator = NewAnimator();

        animator.ApplyDrive(Drive(anchorX: -0.8, anchorY: 0.5, movement: 0.3));
        RunFor(animator, 12.0);

        // Saccades jitter AROUND Core's anchor: the gaze must spend its life in the
        // anchor's half-plane — TOLIK attends where Core says it attends.
        Assert.InRange(animator.Parameters.PupilOffsetX, -1.0, -0.2);
    }

    [Fact]
    public void Drive_keeps_procedural_micro_life_alive()
    {
        CephalonAnimator animator = NewAnimator();

        animator.ApplyDrive(Drive()); // Core's neutral idle
        RunFor(animator, 3.0); // settle onto the driven baseline

        double blinkMin = 1.0;
        double offsetX = 0.0;
        double glowBaseline = animator.Parameters.GlowIntensity;
        double pulseGlow = glowBaseline;
        RunFor(animator, 12.0, _ =>
        {
            blinkMin = Math.Min(blinkMin, animator.Parameters.EyeOpen);
            offsetX = Math.Max(offsetX, Math.Abs(animator.Parameters.PupilOffsetX));
            pulseGlow = Math.Max(pulseGlow, animator.Parameters.GlowIntensity);
        });

        Assert.True(blinkMin < 0.8, "shutter blinks must still fire under a drive");
        Assert.True(offsetX > 0.02, "saccade wander must still fire under a drive");
        Assert.True(pulseGlow > glowBaseline + 0.02,
            "energy pulses must still fire on top of the glow baseline");
    }

    // ---------- the honest fallback ----------

    [Fact]
    public void ClearDrive_returns_to_the_phase1_procedural_idle()
    {
        CephalonAnimator animator = NewAnimator();

        animator.ApplyDrive(Drive(CephalonState.Speaking, eyeOpen: 0.1, glow: 0.9));
        RunFor(animator, 3.0);
        Assert.True(animator.HasDrive);

        animator.ClearDrive();

        Assert.False(animator.HasDrive);
        Assert.Equal(CephalonState.Idle, animator.State);
        Assert.Equal(CephalonTone.Calm, animator.Traits.Tone);
        Assert.Equal(StateTraitTable.GetSafe(CephalonState.Idle), animator.Traits);

        // Back to the honest Phase 1 idle: sample a window (a blink or pulse may
        // occupy any single instant) and check the body LIVES at the idle traits.
        RunFor(animator, 4.0);
        double eyeMax = 0.0;
        double glowMax = 0.0;
        double glowMin = 1.0;
        RunFor(animator, 3.0, _ =>
        {
            eyeMax = Math.Max(eyeMax, animator.Parameters.EyeOpen);
            glowMax = Math.Max(glowMax, animator.Parameters.GlowIntensity);
            glowMin = Math.Min(glowMin, animator.Parameters.GlowIntensity);
        });
        Assert.True(eyeMax >= 0.95, $"idle eye must reopen fully, saw max {eyeMax:0.00}");
        Assert.InRange(glowMax, 0.40, 0.75);   // idle glow baseline, pulses may add
        Assert.InRange(glowMin, 0.30, 0.55);
    }

    [Fact]
    public void Core_locked_mode_renders_dimmed_and_still()
    {
        CephalonAnimator animator = NewAnimator();

        // Core's locked composition: eyeOpenness 0.25, movement 0.05.
        animator.ApplyDrive(Drive(CephalonState.Locked, eyeOpen: 0.25, movement: 0.05, glow: 0.15));
        RunFor(animator, 4.0);

        Assert.Equal(CephalonTone.Locked, animator.Traits.Tone);
        Assert.InRange(animator.Parameters.EyeOpen, 0.15, 0.30);
        Assert.True(animator.Parameters.GlowIntensity <= 0.35,
            "locked must render dimmed, not alarmed");
    }

    [Fact]
    public void Parameters_stay_bounded_under_a_fully_energized_drive()
    {
        CephalonAnimator animator = NewAnimator();

        animator.ApplyDrive(Drive(
            CephalonState.Alert,
            eyeOpen: 1.0, pupilScale: 1.4, glow: 1.0, particles: 1.0,
            shellTilt: CephalonParameters.ShellTiltMax,
            expansionCenter: CephalonParameters.ShellExpansionMax,
            wobble: CephalonParameters.WobbleMax,
            drift: CephalonParameters.DriftMax,
            spectrum: 1.0, movement: 1.0, transition: 1.0));

        for (int i = 0; i < 1800; i++)
        {
            animator.Update(1.0 / 60.0);
            CephalonParameters p = animator.Parameters;
            Assert.InRange(p.EyeOpen, CephalonParameters.EyeOpenMin, CephalonParameters.EyeOpenMax);
            Assert.InRange(p.PupilScale, CephalonParameters.PupilScaleMin, CephalonParameters.PupilScaleMax);
            Assert.InRange(p.GlowIntensity, CephalonParameters.GlowMin, CephalonParameters.GlowMax);
            Assert.InRange(p.ParticleEnergy, CephalonParameters.ParticleMin, CephalonParameters.ParticleMax);
            Assert.InRange(p.ShellExpansion, CephalonParameters.ShellExpansionMin, CephalonParameters.ShellExpansionMax);
            Assert.InRange(p.ShellTilt, CephalonParameters.ShellTiltMin, CephalonParameters.ShellTiltMax);
            Assert.InRange(p.WobbleAmp, CephalonParameters.WobbleMin, CephalonParameters.WobbleMax);
            Assert.InRange(p.DriftMultiplier, CephalonParameters.DriftMin, CephalonParameters.DriftMax);
            Assert.InRange(p.SpectrumIntensity, CephalonParameters.SpectrumMin, CephalonParameters.SpectrumMax);
        }
    }
}
