using TOLIK.Contracts;

namespace TOLIK.Core;

/// <summary>Composer configuration (директива §5/§16): the transition rate multipliers
/// and the base intensities — configurable, validated (positive, finite; else defaults).</summary>
public sealed class ComposerOptions
{
    /// <summary>Per-parameter approach rates (1/s): fast for light-reactive, slow for form.</summary>
    public double FastRate { get; init; } = ParsePositive("TOLIK_CEPHALON_FAST_RATE") ?? 10.0;
    public double MediumRate { get; init; } = ParsePositive("TOLIK_CEPHALON_MEDIUM_RATE") ?? 5.0;
    public double SlowRate { get; init; } = ParsePositive("TOLIK_CEPHALON_SLOW_RATE") ?? 2.0;
    public double IdleGlow { get; init; } = ParsePositive("TOLIK_CEPHALON_IDLE_GLOW") ?? 0.25;
    public double IdlePulse { get; init; } = ParsePositive("TOLIK_CEPHALON_IDLE_PULSE") ?? 0.08;

    private static double? ParsePositive(string variable)
    {
        return double.TryParse(Environment.GetEnvironmentVariable(variable), out var v) &&
               double.IsFinite(v) && v > 0.0
            ? v
            : null;
    }
}

/// <summary>
/// Expression Composer (директива §5, CE-2): transforms the Expression State into the
/// renderer's visual-behavior parameters. DETERMINISTIC and pure: (state, previous, dt)
/// → output — no LLM, no clock, no randomness (the bounded idle stochasticity lives in
/// the renderer's seeded generator). Priority decides the dominant MODE; the parameters
/// ACCUMULATE from every active source so amusement + irritation + speaking produce ONE
/// blended expression (директива §3) — never three clips at once. Transitions are
/// continuous: exponential approach per parameter (спокойствие → раздражение никогда
/// не мгновенно, директива §5/§10). Clamped, no NaN — the renderer trusts the output.
/// </summary>
public static class ExpressionComposer
{
    public static ExpressionOutput Compose(
        ExpressionState state,
        ExpressionOutput? previous,
        double deltaSeconds,
        ComposerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        var opts = options ?? new ComposerOptions();
        var dt = double.IsFinite(deltaSeconds) ? Math.Clamp(deltaSeconds, 0.0, 1.0) : 0.0;
        var s = state.Normalized(); // fail closed to neutral on garbage (§18)
        var prev = previous is { IsFinite: true } p ? p.Clamped() : ExpressionOutput.Neutral;

        // ── Absolute priority: Core LOCKED — fail-closed visual (still, dimmed). ──
        if (s.Locked)
        {
            return Blend(prev, Target(
                eyeOpenness: 0.25, glow: 0.15, movement: 0.05, pulse: 0.02,
                mode: ExpressionMode.Locked, transitionEnergy: 0.15), dt, opts);
        }

        // ── Accumulating blend: every active source contributes bounded deltas. ──
        var target = ExpressionOutput.Neutral with
        {
            TransitionEnergy = 0.3,
            Mode = ExpressionMode.Idle
        };

        // Cognitive/emotional baseline (idle dynamics below amplify/dampen these).
        var attentionIntensity = s.Attention;
        var mode = ExpressionMode.Idle;

        // Task: calm working state — concentration makes movement precise and slow.
        if (s.Processing > 0.2)
        {
            mode = ExpressionMode.Working;
            target = target with
            {
                MovementSpeed = 0.25 + s.Concentration * -0.05 + 0.1,
                Asymmetry = Math.Max(target.Asymmetry, 0.05 * (1 - s.Concentration)),
                EyeOpenness = 0.7,
                PulseAmplitude = 0.06 + s.TaskProgress * 0.08, // the subtle progress pulse
                GlowIntensity = Math.Max(target.GlowIntensity, 0.3)
            };
        }

        // Recovery: focused, slightly uncertain.
        if (s.Recovery > 0.2)
        {
            mode = mode is ExpressionMode.Idle ? ExpressionMode.Recovering : mode;
            target = target with
            {
                MovementSpeed = Math.Max(target.MovementSpeed, 0.35),
                EyeOpenness = Math.Min(target.EyeOpenness, 0.6),
                GlowIntensity = Math.Max(target.GlowIntensity, 0.4),
                TransitionEnergy = Math.Max(target.TransitionEnergy, 0.4)
            };
        }

        // Thinking: the eye wanders, form is slightly brighter.
        if (s.Thinking > 0.3)
        {
            mode = mode is ExpressionMode.Idle or ExpressionMode.Working ? ExpressionMode.Thinking : mode;
            target = target with
            {
                EyeScale = Math.Max(target.EyeScale, 1.02 + s.Thinking * 0.05),
                EyeOpenness = Math.Min(target.EyeOpenness, 0.8),
                GlowIntensity = Math.Max(target.GlowIntensity, 0.35 + s.Thinking * 0.1),
                MovementSpeed = Math.Max(target.MovementSpeed, 0.3)
            };
        }

        // Emotions accumulate (never exclusive):
        target = target with
        {
            // Curiosity: exploratory rotation + a wider eye.
            EyeScale = Math.Max(target.EyeScale, 1.0 + s.Curiosity * 0.12),
            Rotation = s.Curiosity * 0.15,
            // Amusement: playful micro-pulses (irregularity is the renderer's jitter).
            PulseAmplitude = Math.Max(target.PulseAmplitude, 0.1 + s.Amusement * 0.35),
            // Frustration/irritation: asymmetry + sharper movement + energy (NOT only color).
            Asymmetry = Math.Max(target.Asymmetry, s.Frustration * 0.45),
            MovementSpeed = Math.Max(target.MovementSpeed, 0.15 + s.Frustration * 0.45),
            TransitionEnergy = Math.Max(target.TransitionEnergy, 0.3 + s.Frustration * 0.4),
            // Confidence/uncertainty shape the glow steadiness.
            GlowIntensity = Math.Clamp(
                Math.Max(target.GlowIntensity, 0.25 + s.Confidence * 0.15) - s.Uncertainty * 0.1, 0.0, 1.0),
            // Attention: where the eye looks (routing only — never an authorization input).
            AttentionIntensity = Math.Max(target.AttentionIntensity, attentionIntensity),
            Mode = mode
        };

        // ── Voice: the REAL spectrum drives the body (директива §9) — overrides mode. ──
        if (s.IsSpeaking)
        {
            mode = ExpressionMode.Speaking;
            target = target with
            {
                // Low frequencies → the outer shell (pulse/deformation).
                ShellDeformation = 0.25 + s.LowBand * 0.5,
                PulseAmplitude = Math.Max(target.PulseAmplitude, 0.15 + s.LowBand * 0.4),
                // Mid frequencies → the central geometry (eye modulation).
                EyeScale = Math.Clamp(Math.Max(target.EyeScale, 1.0 + s.MidBand * 0.15), 0.8, 1.3),
                EyeOpenness = Math.Max(target.EyeOpenness, 0.85),
                // High frequencies → the thin energy elements/particles.
                ParticleDensity = 0.15 + s.HighBand * 0.6,
                // Total amplitude → movement + glow intensity (normalized, bounded).
                SpectrumIntensity = s.Amplitude,
                GlowIntensity = Math.Max(target.GlowIntensity, 0.4 + s.Amplitude * 0.3),
                MovementSpeed = Math.Max(target.MovementSpeed, 0.5 + s.Amplitude * 0.3),
                TransitionEnergy = Math.Max(target.TransitionEnergy, 0.5),
                Mode = mode
            };
        }

        // ── System signals: the alarm blend — Core's priority, bounded visually (§10). ──
        if (s.SignalLevel is SystemSignalLevel.Error or SystemSignalLevel.Critical)
        {
            mode = ExpressionMode.Alert;
            target = target with
            {
                GlowIntensity = Math.Max(target.GlowIntensity, 0.55),
                Asymmetry = Math.Max(target.Asymmetry, 0.3),
                PulseAmplitude = Math.Max(target.PulseAmplitude, 0.45),
                EyeOpenness = Math.Max(target.EyeOpenness, 0.85),
                MovementSpeed = Math.Max(target.MovementSpeed, 0.5),
                TransitionEnergy = Math.Max(target.TransitionEnergy, 0.6),
                Mode = mode
            };
        }
        else if (s.SignalLevel == SystemSignalLevel.Warning)
        {
            mode = mode is ExpressionMode.Idle ? ExpressionMode.Alert : mode;
            target = target with
            {
                GlowIntensity = Math.Max(target.GlowIntensity, 0.4),
                PulseAmplitude = Math.Max(target.PulseAmplitude, 0.25),
                AttentionIntensity = Math.Max(target.AttentionIntensity, 0.5),
                Mode = mode
            };
        }

        // Idle dynamics: quiet presence — never a statue, never a loop (§7: the
        // stochastic micro-movements are the renderer's bounded generator). This block
        // only FILLS the baseline — it never caps active emotional dynamics (frustration's
        // sharper movement survives; the movement floor is the Neutral baseline itself).
        if (mode is ExpressionMode.Idle && !s.IsSpeaking)
        {
            target = target with
            {
                GlowIntensity = Math.Clamp(opts.IdleGlow + s.Engagement * 0.1, 0.0, 1.0),
                PulseAmplitude = Math.Max(target.PulseAmplitude, opts.IdlePulse),
                TransitionEnergy = 0.25
            };
        }

        return Blend(prev, target.Clamped(), dt, opts);
    }

    /// <summary>Continuous transition (директива §5/§10): the per-parameter exponential
    /// approach — light-reactive parameters move fast, form parameters move slow.</summary>
    private static ExpressionOutput Blend(ExpressionOutput prev, ExpressionOutput target, double dt, ComposerOptions opts)
    {
        var transition = 0.5 + target.TransitionEnergy * 0.5; // transitionEnergy scales the speed
        return new ExpressionOutput(
            Approach(prev.EyeScale, target.EyeScale, dt, opts.SlowRate * transition),
            Approach(prev.EyeOpenness, target.EyeOpenness, dt, opts.MediumRate * transition),
            Approach(prev.EyeRotation, target.EyeRotation, dt, opts.SlowRate * transition),
            Approach(prev.Asymmetry, target.Asymmetry, dt, opts.MediumRate * transition),
            Approach(prev.ShellExpansion, target.ShellExpansion, dt, opts.SlowRate * transition),
            Approach(prev.ShellDeformation, target.ShellDeformation, dt, opts.FastRate * transition),
            Approach(prev.Rotation, target.Rotation, dt, opts.SlowRate * transition),
            Approach(prev.PulseAmplitude, target.PulseAmplitude, dt, opts.FastRate * transition),
            Approach(prev.GlowIntensity, target.GlowIntensity, dt, opts.FastRate * transition),
            Approach(prev.ParticleDensity, target.ParticleDensity, dt, opts.MediumRate * transition),
            Approach(prev.SpectrumIntensity, target.SpectrumIntensity, dt, opts.FastRate * transition),
            Approach(prev.MovementSpeed, target.MovementSpeed, dt, opts.MediumRate * transition),
            Approach(prev.AttentionAngle, target.AttentionAngle, dt, opts.SlowRate * transition),
            Approach(prev.AttentionIntensity, target.AttentionIntensity, dt, opts.MediumRate * transition),
            Approach(prev.TransitionEnergy, target.TransitionEnergy, dt, opts.MediumRate * transition),
            target.Mode).Clamped();
    }

    private static ExpressionOutput Target(
        double eyeOpenness, double glow, double movement, double pulse,
        ExpressionMode mode, double transitionEnergy) => new(
        EyeScale: 1.0, EyeOpenness: eyeOpenness, EyeRotation: 0,
        Asymmetry: 0, ShellExpansion: 0, ShellDeformation: 0,
        Rotation: 0, PulseAmplitude: pulse, GlowIntensity: glow,
        ParticleDensity: 0.05, SpectrumIntensity: 0, MovementSpeed: movement,
        AttentionAngle: 0, AttentionIntensity: 0, TransitionEnergy: transitionEnergy,
        Mode: mode);

    private static double Approach(double current, double target, double dt, double rate)
    {
        var t = 1.0 - Math.Exp(-rate * Math.Max(0.0, dt));
        return current + (target - current) * Math.Clamp(t, 0.0, 1.0);
    }
}
