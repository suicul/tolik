namespace TOLIK.Desktop.Motion;

/// <summary>
/// Procedural motion core. Turns (state traits + bounded stochastic idle actions +
/// continuous breathing/drift) into a smoothly-eased <see cref="CephalonParameters"/>.
///
/// Design invariants:
/// - No pre-baked mood animations: motion is composed live from short procedural
///   primitives (saccade, blink, pulse, drift retarget, wobble retarget).
/// - Controlled stochasticity: every interval and amplitude comes from
///   <see cref="IdleMotionConfig"/> bounds via the seeded <see cref="IRandom"/>;
///   trajectories are deterministic per seed and never a fixed loop.
/// - Smooth everything: state changes and targets are eased, never snapped.
/// - Bounded everything: <see cref="CephalonParameters.Clamp"/> runs every update.
/// </summary>
public sealed class CephalonAnimator
{
    private const double MaxDt = 0.25; // clamp huge hiccups so easing stays stable

    // Per-parameter easing rates (1/s). Higher = snappier.
    private const double RateEyeOpen = 10.0;
    private const double RatePupil = 6.0;
    private const double RatePupilScale = 4.0;
    private const double RateGlow = 3.0;
    private const double RateParticle = 2.5;
    private const double RateExpansion = 2.5;
    private const double RateTilt = 1.2;
    private const double RateWobble = 1.0;
    private const double RateDrift = 0.8;

    private readonly IdleMotionConfig _cfg;
    private readonly IRandom _rand;
    private readonly CephalonParameters _target = new();

    private CephalonState _state = CephalonState.Idle;
    private StateTraits _traits = StateTraitTable.GetSafe(CephalonState.Idle);
    private double _time;

    private double _nextSaccadeAt;
    private double _nextBlinkAt;
    private double _nextPulseAt;
    private double _nextDriftAt;
    private double _nextWobbleAt;

    private double _pupilTargetX;
    private double _pupilTargetY;
    private double _scaleTarget;
    private double _driftTarget = 1.0;
    private double _wobbleTarget;

    private double _pulseAmplitude;
    private double _pulseStart = -1.0;
    private double _blinkStart = -1.0;

    private int _wobbleHarmonics = 3;

    public CephalonAnimator(IdleMotionConfig config, IRandom random, CephalonState initialState = CephalonState.Idle)
    {
        _cfg = config ?? throw new ArgumentNullException(nameof(config));
        _rand = random ?? throw new ArgumentNullException(nameof(random));
        SetState(initialState);
        _scaleTarget = _traits.PupilScale;
        _nextSaccadeAt = _rand.Next(_cfg.SaccadeIntervalMin, _cfg.SaccadeIntervalMax);
        _nextBlinkAt = _rand.Next(_cfg.BlinkIntervalMin, _cfg.BlinkIntervalMax);
        _nextPulseAt = _rand.Next(_cfg.PulseIntervalMin, _cfg.PulseIntervalMax);
        _nextDriftAt = _rand.Next(_cfg.DriftIntervalMin, _cfg.DriftIntervalMax);
        _nextWobbleAt = _rand.Next(_cfg.WobbleIntervalMin, _cfg.WobbleIntervalMax);
    }

    /// <summary>Current output parameters (mutated in place; reference is stable).</summary>
    public CephalonParameters Parameters { get; } = new();

    public CephalonState State => _state;

    /// <summary>Traits of the current state (tone consumed by the renderer).</summary>
    public StateTraits Traits => _traits;

    public double TimeSeconds => _time;

    /// <summary>Harmonic count of the deformable shell wobble (retargeted occasionally).</summary>
    public int WobbleHarmonics => _wobbleHarmonics;

    /// <summary>
    /// Global motion scale from the mode policy (IdleIntensity; 0 in dormant modes).
    /// Scales stochastic amplitudes and rotation speeds. Kept in [0, 1.2].
    /// </summary>
    public double MotionScale { get; set; } = 1.0;

    /// <summary>
    /// Switches expression state. The body eases toward the new state's traits —
    /// never snaps. Phase 2/3 will call this from the Core event bridge; the
    /// signature stays the same.
    /// </summary>
    public void SetState(CephalonState state)
    {
        _state = state;
        _traits = StateTraitTable.GetSafe(state);
        // Re-anchor focus to the new state's pupil trait; the next saccade will
        // vary it around this anchor. The body eases toward it — never snaps.
        _scaleTarget = _traits.PupilScale;
    }

    /// <summary>Advances the body by <paramref name="dtSeconds"/> (real elapsed time,
    /// clamped to [0, 0.25] for stability). Deterministic per seed.</summary>
    public void Update(double dtSeconds)
    {
        double dt = double.IsFinite(dtSeconds) ? Math.Clamp(dtSeconds, 0.0, MaxDt) : 0.0;
        _time += dt;
        double motion = Math.Clamp(MotionScale, 0.0, 1.2);
        double dtSafe = Math.Max(dt, 1e-4);

        RunScheduledActions(dt, motion);
        (double blinkEnv, double pulseNorm) = ComputeEnvelopes();
        UpdateTargets(blinkEnv, pulseNorm, motion);

        CephalonParameters p = Parameters;

        // Layer rotations integrate continuously (bounded by wrap in Clamp).
        p.ShellRotation += _cfg.BaseOuterOmega * p.DriftMultiplier * motion * dt;
        p.Shell2Rotation += _cfg.BaseMiddleOmega * p.DriftMultiplier * motion * dt;
        p.BracketRotation += _cfg.BaseBracketOmega * p.DriftMultiplier * motion * dt;
        p.IrisRotation += _cfg.BaseIrisOmega * p.DriftMultiplier * motion * dt;
        p.WobblePhase += 0.22 * motion * dt;

        // Ease everything toward targets — no snapping, ever.
        Ease(ref p.EyeOpen, _target.EyeOpen, RateEyeOpen, dtSafe);
        Ease(ref p.PupilOffsetX, _target.PupilOffsetX, RatePupil, dtSafe);
        Ease(ref p.PupilOffsetY, _target.PupilOffsetY, RatePupil, dtSafe);
        Ease(ref p.PupilScale, _target.PupilScale, RatePupilScale, dtSafe);
        Ease(ref p.GlowIntensity, _target.GlowIntensity, RateGlow, dtSafe);
        Ease(ref p.ParticleEnergy, _target.ParticleEnergy, RateParticle, dtSafe);
        Ease(ref p.ShellExpansion, _target.ShellExpansion, RateExpansion, dtSafe);
        Ease(ref p.ShellTilt, _target.ShellTilt, RateTilt, dtSafe);
        Ease(ref p.WobbleAmp, _target.WobbleAmp, RateWobble, dtSafe);
        Ease(ref p.DriftMultiplier, _driftTarget, RateDrift, dtSafe);

        p.Clamp();
    }

    private void RunScheduledActions(double dt, double motion)
    {
        // NOTE: dt is currently unused by the individual actions, but the parameter
        // keeps the signature stable for future continuous actions.
        _ = dt;

        if (_time >= _nextSaccadeAt)
        {
            DoSaccade(motion);
            _nextSaccadeAt = _time + _rand.Next(_cfg.SaccadeIntervalMin, _cfg.SaccadeIntervalMax);
        }

        if (_time >= _nextBlinkAt)
        {
            // The shutter blink is a micro-expression: in fully frozen (dormant)
            // modes it must not fire — Hidden mode is still, not twitching.
            if (motion > 0.02)
            {
                _blinkStart = _time;
            }
            _nextBlinkAt = _time + _rand.Next(_cfg.BlinkIntervalMin, _cfg.BlinkIntervalMax);
        }

        if (_time >= _nextPulseAt)
        {
            _pulseAmplitude = _cfg.PulseMaxAmplitude * motion * _rand.Next(0.5, 1.0);
            _pulseStart = _time;
            _nextPulseAt = _time + _rand.Next(_cfg.PulseIntervalMin, _cfg.PulseIntervalMax);
        }

        if (_time >= _nextDriftAt)
        {
            // Orientation drift: keep a small floor so a dormant-looking drift target
            // can't fully stall the slow layer rotation for long stretches.
            _driftTarget = _rand.Next(-1.0, 1.0) * _cfg.DriftMaxVelocity * Math.Max(motion, 0.15);
            _nextDriftAt = _time + _rand.Next(_cfg.DriftIntervalMin, _cfg.DriftIntervalMax);
        }

        if (_time >= _nextWobbleAt)
        {
            _wobbleTarget = _cfg.WobbleMaxAmplitude * motion * _rand.Next(0.15, 1.0);
            _wobbleHarmonics = _rand.NextInt(2, 6);
            _nextWobbleAt = _time + _rand.Next(_cfg.WobbleIntervalMin, _cfg.WobbleIntervalMax);
        }
    }

    private void DoSaccade(double motion)
    {
        if (motion <= 0.02)
        {
            _pupilTargetX = 0.0;
            _pupilTargetY = 0.0;
            return;
        }

        if (_rand.NextDouble() < _cfg.SaccadeCenterChance)
        {
            _pupilTargetX = 0.0;
            _pupilTargetY = 0.0;
        }
        else
        {
            // sqrt for uniform disc coverage; amplitude bounded by config * motion.
            double r = _cfg.SaccadeMaxOffset * motion * Math.Sqrt(_rand.NextDouble());
            double a = _rand.NextDouble() * (Math.PI * 2.0);
            _pupilTargetX = r * Math.Cos(a);
            _pupilTargetY = r * Math.Sin(a);
        }

        // Focus change accompanies most gaze shifts, anchored on the state's
        // pupil trait (a dilated state stays dilated — focus only varies around it).
        _scaleTarget = _traits.PupilScale * _rand.Next(0.92, 1.12);
    }

    private (double BlinkEnv, double PulseNorm) ComputeEnvelopes()
    {
        double blinkEnv = 1.0;
        if (_blinkStart >= 0.0)
        {
            double t = _time - _blinkStart;
            if (t >= _cfg.BlinkDuration)
            {
                _blinkStart = -1.0;
            }
            else
            {
                double phase = t / _cfg.BlinkDuration;
                blinkEnv = 1.0 - (1.0 - _cfg.BlinkCloseLevel) * Math.Sin(Math.PI * phase);
            }
        }

        double pulseNorm = 0.0;
        if (_pulseStart >= 0.0)
        {
            double t = _time - _pulseStart;
            pulseNorm = Math.Exp(-t * _cfg.PulseReleaseRate);
            if (pulseNorm < 0.01)
            {
                pulseNorm = 0.0;
                _pulseStart = -1.0;
            }
        }

        return (blinkEnv, pulseNorm);
    }

    private void UpdateTargets(double blinkEnv, double pulseNorm, double motion)
    {
        _target.EyeOpen = _traits.EyeOpen * blinkEnv;
        _target.PupilOffsetX = _pupilTargetX;
        _target.PupilOffsetY = _pupilTargetY;
        _target.PupilScale = _scaleTarget;
        _target.GlowIntensity = _traits.GlowIntensity + _pulseAmplitude * pulseNorm;
        _target.ParticleEnergy = _traits.ParticleEnergy * (0.8 + 0.5 * pulseNorm);
        _target.ShellExpansion = 1.0 + _cfg.BreathingAmplitude * Math.Sin((Math.PI * 2.0) * _time / _cfg.BreathingPeriodSec);
        _target.ShellTilt = _cfg.TiltAmplitude * Math.Sin((Math.PI * 2.0) * _time / _cfg.TiltPeriodSec);
        _target.WobbleAmp = _wobbleTarget;

        // Breathing/tilt still breathe faintly at low motion so Hidden mode reads
        // as "dormant", not "crashed" — amplitude itself is policy-scaled above.
        if (motion <= 0.0)
        {
            _target.ShellExpansion = 1.0;
            _target.ShellTilt = 0.0;
        }
    }

    private static void Ease(ref double current, double target, double rate, double dt)
    {
        double k = 1.0 - Math.Exp(-rate * dt);
        current += (target - current) * k;
    }
}
