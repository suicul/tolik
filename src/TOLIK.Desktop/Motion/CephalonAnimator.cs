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
    private const double RateSpectrum = 8.0; // light-reactive: voice bands must be quick (CE-3)

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
    private double _focusJitter = 1.0;
    private double _driftTarget = 1.0;
    private double _wobbleTarget;

    private double _pulseAmplitude;
    private double _pulseStart = -1.0;
    private double _blinkStart = -1.0;

    private int _wobbleHarmonics = 3;

    /// <summary>Live Core expression drive (CE-2); null → pure procedural idle.</summary>
    private ExpressionDrive? _drive;

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

    /// <summary>True while a Core expression drive owns the body's targets.</summary>
    public bool HasDrive => _drive is not null;

    /// <summary>
    /// CE-2: adopts a Core expression drive (already mapped into animator space).
    /// The drive sets TARGETS — the body eases there; procedural micro-life
    /// (saccades, blinks, pulses) composes ON TOP so TOLIK never becomes a puppet
    /// on strings. Nothing snaps, ever.
    /// </summary>
    public void ApplyDrive(ExpressionDrive drive)
    {
        ArgumentNullException.ThrowIfNull(drive);
        _drive = drive;
        SetState(drive.State);
    }

    /// <summary>
    /// Drops the Core drive and returns to the honest fallback: the procedural idle
    /// in the calm Idle state (no events flowing → local idle, never a frozen
    /// expression pretending Core is still talking).
    /// </summary>
    public void ClearDrive()
    {
        _drive = null;
        SetState(CephalonState.Idle);
        _scaleTarget = _traits.PupilScale;
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
    /// never snaps. CE-2: reached through <see cref="ApplyDrive"/> (the Core event
    /// bridge maps mode → state); the signature stays the same.
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
        double dtSafe = Math.Max(dt, 1e-4);

        ExpressionDrive? drive = _drive;

        // Effective motion: policy scale (presence mode) × state traits × Core's
        // movement energy (drive present) — idle stochasticity stays alive but
        // calibrated by whatever Core reports.
        double baseMotion = Math.Clamp(MotionScale, 0.0, 1.2);
        double motion = drive is null
            ? baseMotion
            : baseMotion * _traits.MotionLevel
                * (0.75 + 0.5 * Math.Clamp(drive.MovementSpeed, 0.0, 1.0));

        // Core's TransitionEnergy modulates how fast the body chases its targets
        // (0.6×..1.4× the procedural rates) — natural modulation, never a snap.
        double rateScale = drive is null
            ? 1.0
            : Math.Clamp(0.6 + 0.8 * Math.Clamp(drive.TransitionEnergy, 0.0, 1.0), 0.6, 1.4);

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
        Ease(ref p.EyeOpen, _target.EyeOpen, RateEyeOpen * rateScale, dtSafe);
        Ease(ref p.PupilOffsetX, _target.PupilOffsetX, RatePupil * rateScale, dtSafe);
        Ease(ref p.PupilOffsetY, _target.PupilOffsetY, RatePupil * rateScale, dtSafe);
        Ease(ref p.PupilScale, _target.PupilScale, RatePupilScale * rateScale, dtSafe);
        Ease(ref p.GlowIntensity, _target.GlowIntensity, RateGlow * rateScale, dtSafe);
        Ease(ref p.ParticleEnergy, _target.ParticleEnergy, RateParticle * rateScale, dtSafe);
        Ease(ref p.ShellExpansion, _target.ShellExpansion, RateExpansion * rateScale, dtSafe);
        Ease(ref p.ShellTilt, _target.ShellTilt, RateTilt * rateScale, dtSafe);
        Ease(ref p.WobbleAmp, _target.WobbleAmp, RateWobble * rateScale, dtSafe);
        // Orientation drift: the drive OWNS the multiplier while it flows (Core's
        // rotation field, 1 = baseline spin preserved); procedural drift otherwise.
        Ease(ref p.DriftMultiplier, drive?.DriftMultiplier ?? _driftTarget, RateDrift * rateScale, dtSafe);
        // Voice spectrum follows the drive (light-reactive rate); exactly 0 without one.
        Ease(ref p.SpectrumIntensity, _target.SpectrumIntensity, RateSpectrum * rateScale, dtSafe);

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
            // Core's pulseAmplitude adds energy on top of the procedural pulse so
            // Core pulse events land as real glow beats, not noise-floor changes.
            _pulseAmplitude = (_cfg.PulseMaxAmplitude * motion * _rand.Next(0.5, 1.0))
                + (0.25 * (_drive?.PulseAmplitude ?? 0.0));
            _pulseStart = _time;
            _nextPulseAt = _time + _rand.Next(_cfg.PulseIntervalMin, _cfg.PulseIntervalMax);
        }

        if (_time >= _nextDriftAt)
        {
            // Orientation drift: keep a small floor so a dormant-looking drift target
            // can't fully stall the slow layer rotation for long stretches.
            // While a Core drive flows, Core's rotation owns the drift — the
            // procedural retarget stands down (schedule stays, body doesn't fight).
            if (_drive is null)
            {
                _driftTarget = _rand.Next(-1.0, 1.0) * _cfg.DriftMaxVelocity * Math.Max(motion, 0.15);
            }
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
        // Gaze anchor: Core attention when a drive flows (0,0 otherwise). Saccades
        // jitter AROUND the anchor — the eye wanders, but where TOLIK attends wins.
        double anchorX = 0.0;
        double anchorY = 0.0;
        double focusBase = _traits.PupilScale;
        if (_drive is { } drive)
        {
            anchorX = Math.Clamp(drive.PupilAnchorX, -1.0, 1.0);
            anchorY = Math.Clamp(drive.PupilAnchorY, -1.0, 1.0);
            focusBase = Math.Clamp(drive.PupilScale, CephalonParameters.PupilScaleMin, CephalonParameters.PupilScaleMax);
        }

        if (motion <= 0.02)
        {
            _pupilTargetX = anchorX;
            _pupilTargetY = anchorY;
            return;
        }

        if (_rand.NextDouble() < _cfg.SaccadeCenterChance)
        {
            _pupilTargetX = anchorX;
            _pupilTargetY = anchorY;
        }
        else
        {
            // sqrt for uniform disc coverage; amplitude bounded by config * motion.
            double r = _cfg.SaccadeMaxOffset * motion * Math.Sqrt(_rand.NextDouble());
            double a = _rand.NextDouble() * (Math.PI * 2.0);
            _pupilTargetX = Math.Clamp(anchorX + (r * Math.Cos(a)), -1.0, 1.0);
            _pupilTargetY = Math.Clamp(anchorY + (r * Math.Sin(a)), -1.0, 1.0);
        }

        // Focus change accompanies most gaze shifts, anchored on the drive's scale
        // (Core eyeScale) or the state's pupil trait — focus only varies around it.
        // The jitter factor is kept separately: while a drive flows, Core's eyeScale
        // is re-adopted on EVERY event (5 Hz), not just on the next saccade.
        _focusJitter = _rand.Next(0.92, 1.12);
        _scaleTarget = focusBase * _focusJitter;
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
        ExpressionDrive? drive = _drive;

        // Eyelid: Core's eyeOpenness owns the aperture while a drive flows (the
        // shutter blink is micro-life composing on top); the state trait owns it
        // otherwise.
        double eyeOpenBase = drive is null ? _traits.EyeOpen : Math.Clamp(drive.EyeOpen, 0.0, 1.0);
        _target.EyeOpen = eyeOpenBase * blinkEnv;

        _target.PupilOffsetX = _pupilTargetX;
        _target.PupilOffsetY = _pupilTargetY;

        // Focus: while a drive flows, Core's eyeScale owns the base and saccades
        // only jitter AROUND it — every event (≈5 Hz) is adopted continuously,
        // never left waiting for the next saccade. The state trait anchors otherwise.
        _target.PupilScale = drive is { } d
            ? Math.Clamp(
                d.PupilScale * _focusJitter,
                CephalonParameters.PupilScaleMin, CephalonParameters.PupilScaleMax)
            : _scaleTarget;

        // Glow: Core's glowIntensity owns the baseline while a drive flows; energy
        // pulses land on top (real beats, not noise-floor changes).
        double glowBase = drive is null ? _traits.GlowIntensity : Math.Clamp(drive.GlowIntensity, 0.0, 1.0);
        _target.GlowIntensity = glowBase + _pulseAmplitude * pulseNorm;

        double particleBase = drive is null ? _traits.ParticleEnergy : Math.Clamp(drive.ParticleEnergy, 0.0, 1.0);
        _target.ParticleEnergy = particleBase * (0.8 + 0.5 * pulseNorm);

        // Breathing: Core's shellExpansion owns the CENTER while a drive flows — the
        // living breath continues around it (never a frozen shell).
        double expansionCenter = drive is null ? 1.0 : drive.ShellExpansionCenter;
        _target.ShellExpansion = expansionCenter
            + _cfg.BreathingAmplitude * Math.Sin((Math.PI * 2.0) * _time / _cfg.BreathingPeriodSec);

        // Silhouette tilt: Core's eyeRotation owns the form while a drive flows; the
        // slow procedural sway otherwise (it stands down, like drift).
        _target.ShellTilt = drive is null
            ? _cfg.TiltAmplitude * Math.Sin((Math.PI * 2.0) * _time / _cfg.TiltPeriodSec)
            : drive.ShellTilt;

        // Shell deformation: the ambient wobble is the renderer's bounded generator
        // (Core's composer §7 delegates stochastic micro-movement to the renderer);
        // Core's shellDeformation ADDS energy on top — a zero request keeps the
        // Phase 1 idle wobble alive, a speaking bassline visibly deforms the shell.
        _target.WobbleAmp = Math.Min(
            (drive is null ? 0.0 : drive.WobbleAmp) + _wobbleTarget,
            CephalonParameters.WobbleMax);

        // Voice spectrum (the CE-3 axis): carried truthfully — EXACTLY zero effect
        // at zero input, real shimmer when CE-3 feeds it live TTS bands.
        _target.SpectrumIntensity = Math.Clamp(drive?.Spectrum ?? 0.0, 0.0, 1.0);

        // Breathing/tilt still breathe faintly at low motion so Hidden mode reads
        // as "dormant", not "crashed" — amplitude itself is policy-scaled above.
        if (motion <= 0.0)
        {
            _target.ShellExpansion = expansionCenter;
            if (drive is null)
            {
                _target.ShellTilt = 0.0;
            }
        }
    }

    private static void Ease(ref double current, double target, double rate, double dt)
    {
        double k = 1.0 - Math.Exp(-rate * dt);
        current += (target - current) * k;
    }
}
