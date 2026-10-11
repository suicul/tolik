package com.tolik.client

import kotlin.math.PI
import kotlin.math.cos
import kotlin.math.exp
import kotlin.math.max
import kotlin.math.min
import kotlin.math.sin
import kotlin.random.Random

// Procedural motion core for the mobile Cephalon — the Android counterpart of
// src/TOLIK.Desktop/Motion/CephalonAnimator.cs + IdleMotionConfig.cs (same
// semantics, amplitudes adapted for a smaller screen).
//
// Design invariants (mirrored from the desktop):
// - No pre-baked mood animations: motion composes live from short procedural
//   primitives (saccade, blink, pulse, drift retarget, wobble retarget).
// - Controlled stochasticity: every interval/amplitude comes from
//   [IdleMotionConfig] bounds via a seeded Random — deterministic per seed,
//   never a fixed noticeable loop, never chaos.
// - Smooth everything: Core events (~5 Hz) set TARGETS; the FPS loop eases
//   toward them — never snaps.
// - Bounded everything: every output value is clamped every update.
//
// NOT a composer (docs/android-app.md §3): the mapping ExpressionState →
// ExpressionOutput is Core-owned. This class eases + decorates the already
// composed wire targets with bounded idle motion, that is all.

/**
 * Bounded intervals/amplitudes of procedural idle motion. Pure data, fully
 * testable (desktop IdleMotionConfig.Default scaled for a phone-sized canvas:
 * slightly slower cadence, smaller offsets/amplitudes).
 */
data class IdleMotionConfig(
    val saccadeIntervalMin: Double = 2.5,
    val saccadeIntervalMax: Double = 7.0,
    val saccadeMaxOffset: Double = 0.45,
    val saccadeCenterChance: Double = 0.35,
    val blinkIntervalMin: Double = 3.5,
    val blinkIntervalMax: Double = 9.0,
    val blinkDuration: Double = 0.35,
    val blinkCloseLevel: Double = 0.15,
    val pulseIntervalMin: Double = 4.5,
    val pulseIntervalMax: Double = 11.0,
    val pulseMaxAmplitude: Double = 0.18,
    val pulseReleaseRate: Double = 2.2,
    val driftIntervalMin: Double = 3.0,
    val driftIntervalMax: Double = 9.0,
    val driftMaxVelocity: Double = 0.9,
    val wobbleIntervalMin: Double = 6.0,
    val wobbleIntervalMax: Double = 14.0,
    val wobbleMaxAmplitude: Double = 0.028,
    val breathingAmplitude: Double = 0.014,
    val breathingPeriodSec: Double = 6.8,
    val tiltAmplitude: Double = 0.016,
    val tiltPeriodSec: Double = 11.3,
    val baseOuterOmega: Double = 0.045,
    val baseMiddleOmega: Double = -0.07,
    val baseBracketOmega: Double = 0.024,
    val baseIrisOmega: Double = 0.01,
) {
    companion object {
        /** Production tuning — desktop Default × mobile adaptation. */
        val Mobile: IdleMotionConfig = IdleMotionConfig()
    }
}

/** Eased visual state the renderer draws (mutated in place by the animator on
 * the render thread only). Every field documents its clamp. */
class CephalonVisualState {
    var eyeOpen: Double = 0.75          // [0..1] eyelid aperture
    var eyeScale: Double = 1.0          // [0.8..1.3]
    var eyeRotation: Double = 0.0       // [-0.3..0.3] rad
    var pupilOffsetX: Double = 0.0      // [-1..1] fraction of pupil travel
    var pupilOffsetY: Double = 0.0      // [-1..1]
    var pupilScale: Double = 1.0        // [0.6..1.6]
    var shellExpansion: Double = 1.0    // [0.75..1.55] shell scale multiplier
    var wobbleAmp: Double = 0.0         // [0..0.06] radial wobble fraction
    var wobblePhase: Double = 0.0
    var shellRotation: Double = 0.0     // rad, wrapped to (-π, π]
    var shell2Rotation: Double = 0.0    // rad
    var bracketRotation: Double = 0.0   // rad
    var irisRotation: Double = 0.0      // rad
    var glow: Double = 0.25             // [0..1]
    var particleEnergy: Double = 0.1    // [0..1]
    var spectrumShimmer: Double = 0.0   // [0..1]
    var tilt: Double = 0.0              // [-0.05..0.05]
    var time: Double = 0.0              // seconds since start
    var wobbleHarmonics: Int = 3        // [2..5]
}

/**
 * Turns (Core ExpressionOutput targets + bounded stochastic idle actions +
 * continuous breathing/drift) into a smoothly-eased [CephalonVisualState].
 * Single-threaded: [setExpression] parks the target, [update] (render thread)
 * consumes it — no locks needed.
 */
class CephalonAnimator(
    val config: IdleMotionConfig = IdleMotionConfig.Mobile,
    random: Random = Random(SEED),
    initialState: ExpressionOutput = ExpressionOutput.NEUTRAL,
) {
    companion object {
        /** Deterministic default seed (same spirit as the desktop 0x5EEDC0DE7). */
        const val SEED: Long = 0x5EEDC0DE7L
        const val MAX_DT = 0.25

        // Per-parameter easing rates (1/s). Higher = snappier. Multiplied by
        // (0.6 + 1.4·transitionEnergy): Core can make transitions calm or crisp.
        private const val RATE_EYE_OPEN = 10.0
        private const val RATE_PUPIL = 6.0
        private const val RATE_EYE_SCALE = 4.0
        private const val RATE_EYE_ROTATION = 4.0
        private const val RATE_GLOW = 3.0
        private const val RATE_PARTICLE = 2.5
        private const val RATE_EXPANSION = 2.5
        private const val RATE_TILT = 1.2
        private const val RATE_WOBBLE = 1.0
        private const val RATE_DRIFT = 0.8
    }

    val state: CephalonVisualState = CephalonVisualState()

    private val cfg = config
    private val rand: Random = random

    private var target: ExpressionOutput = initialState.normalized()
    private var pendingExpression: ExpressionOutput? = null

    private var nextSaccadeAt: Double
    private var nextBlinkAt: Double
    private var nextPulseAt: Double
    private var nextDriftAt: Double
    private var nextWobbleAt: Double

    private var pupilTargetX: Double = 0.0
    private var pupilTargetY: Double = 0.0
    private var driftTarget: Double = 1.0
    private var wobbleTarget: Double = 0.0

    private var pulseAmplitude: Double = 0.0
    private var pulseStart: Double = -1.0
    private var blinkStart: Double = -1.0

    init {
        nextSaccadeAt = rand.nextDouble(cfg.saccadeIntervalMin, cfg.saccadeIntervalMax)
        nextBlinkAt = rand.nextDouble(cfg.blinkIntervalMin, cfg.blinkIntervalMax)
        nextPulseAt = rand.nextDouble(cfg.pulseIntervalMin, cfg.pulseIntervalMax)
        nextDriftAt = rand.nextDouble(cfg.driftIntervalMin, cfg.driftIntervalMax)
        nextWobbleAt = rand.nextDouble(cfg.wobbleIntervalMin, cfg.wobbleIntervalMax)
    }

    /** Tone of the current expression (color vocabulary for the renderer). */
    val tone: CephalonTone
        get() = CephalonPalette.toneFor(target.mode)

    val mode: ExpressionMode
        get() = target.mode

    /** Thread-safe target handoff: the render loop picks it up on next update. */
    fun setExpression(expression: ExpressionOutput) {
        pendingExpression = expression.normalized()
    }

    /** Advances the body by dtSeconds (real elapsed time, clamped to [0, 0.25]).
     * Deterministic per seed. */
    fun update(dtSeconds: Double) {
        pendingExpression?.let { target = it }
        pendingExpression = null

        val dt = if (dtSeconds.isFinite()) dtSeconds.coerceIn(0.0, MAX_DT) else 0.0
        state.time += dt
        val dtSafe = max(dt, 1e-4)

        val expr = target
        val motion = expr.movementSpeed.coerceIn(0.0, 1.0)

        runScheduledActions(motion)
        val (blinkEnv, pulseNorm) = computeEnvelopes()

        // Targets from Core + idle layering, then the alarm render policy.
        var eyeOpenTarget = expr.eyeOpenness * blinkEnv
        var glowTarget = expr.glowIntensity + pulseAmplitude * pulseNorm
        var particleTarget = expr.particleDensity * (0.8 + 0.5 * pulseNorm)
        var motionEff = motion
        val policy = CephalonModeRenderPolicy.applyToTargets(
            mode = expr.mode,
            eyeOpen = eyeOpenTarget,
            motion = motionEff,
            particles = particleTarget,
            glow = glowTarget,
        )
        eyeOpenTarget = policy.eyeOpen
        motionEff = policy.motion
        particleTarget = policy.particles
        glowTarget = policy.glow

        // Gaze: Core attention vector + bounded stochastic saccade.
        val attention = expr.attentionIntensity.coerceIn(0.0, 1.0)
        val ax = cos(expr.attentionAngle) * attention
        val ay = sin(expr.attentionAngle) * attention
        val px = (ax + pupilTargetX).coerceIn(-1.0, 1.0)
        val py = (ay + pupilTargetY).coerceIn(-1.0, 1.0)

        val rateScale = 0.6 + 1.4 * expr.transitionEnergy.coerceIn(0.0, 1.0)
        val s = state

        // Layer rotations integrate continuously (wrapped to (-π, π]).
        val rot = expr.rotation.coerceIn(-1.0, 1.0)
        val drift = s.driftMultiplier(motionEff)
        s.shellRotation = wrap(s.shellRotation + (cfg.baseOuterOmega * drift + rot * 0.5) * dtSafe)
        s.shell2Rotation = wrap(s.shell2Rotation + (cfg.baseMiddleOmega * drift - rot * 0.35) * dtSafe)
        s.bracketRotation = wrap(s.bracketRotation + (cfg.baseBracketOmega * drift * 0.8) * dtSafe)
        s.irisRotation = wrap(s.irisRotation + (cfg.baseIrisOmega - rot * 0.15) * dtSafe)
        s.wobblePhase += 0.22 * motionEff * dtSafe

        // Ease everything toward targets — no snapping, ever.
        s.eyeOpen = ease(s.eyeOpen, eyeOpenTarget, RATE_EYE_OPEN * rateScale, dtSafe)
        s.eyeScale = ease(s.eyeScale, expr.eyeScale.coerceIn(0.8, 1.3), RATE_EYE_SCALE * rateScale, dtSafe)
        s.eyeRotation = ease(
            s.eyeRotation,
            expr.eyeRotation.coerceIn(-0.3, 0.3),
            RATE_EYE_ROTATION * rateScale,
            dtSafe,
        )
        s.pupilOffsetX = ease(s.pupilOffsetX, px, RATE_PUPIL * rateScale, dtSafe)
        s.pupilOffsetY = ease(s.pupilOffsetY, py, RATE_PUPIL * rateScale, dtSafe)
        s.pupilScale = ease(s.pupilScale, 0.9 + 0.35 * expr.pulseAmplitude, RATE_PUPIL, dtSafe)
        s.glow = ease(s.glow, glowTarget.coerceIn(0.0, 1.0), RATE_GLOW * rateScale, dtSafe)
        s.particleEnergy = ease(s.particleEnergy, particleTarget.coerceIn(0.0, 1.0), RATE_PARTICLE * rateScale, dtSafe)
        s.spectrumShimmer = ease(s.spectrumShimmer, expr.spectrumIntensity.coerceIn(0.0, 1.0), RATE_GLOW * rateScale, dtSafe)
        s.shellExpansion = ease(
            s.shellExpansion,
            1.0 + expr.shellExpansion + cfg.breathingAmplitude * sin(2.0 * PI * state.time / cfg.breathingPeriodSec),
            RATE_EXPANSION * rateScale,
            dtSafe,
        )
        s.wobbleAmp = ease(
            s.wobbleAmp,
            min(0.06, wobbleTarget + expr.shellDeformation * cfg.wobbleMaxAmplitude * 1.6),
            RATE_WOBBLE * rateScale,
            dtSafe,
        )
        s.tilt = ease(
            s.tilt,
            cfg.tiltAmplitude * sin(2.0 * PI * state.time / cfg.tiltPeriodSec),
            RATE_TILT * rateScale,
            dtSafe,
        )
        clampState(s)
    }

    /** Drift multiplier for base rotation speeds (bounded ±[DriftMaxVelocity]). */
    private fun CephalonVisualState.driftMultiplier(motion: Double): Double =
        driftTarget.coerceIn(-cfg.driftMaxVelocity, cfg.driftMaxVelocity) * max(motion, 0.15)

    private fun runScheduledActions(motion: Double) {
        val t = state.time
        if (t >= nextSaccadeAt) {
            doSaccade(motion)
            nextSaccadeAt = t + rand.nextDouble(cfg.saccadeIntervalMin, cfg.saccadeIntervalMax)
        }
        if (t >= nextBlinkAt) {
            // The shutter blink is a micro-expression: in near-frozen states it
            // must not fire — locked/dormant is still, not twitching.
            if (motion > 0.02) blinkStart = t
            nextBlinkAt = t + rand.nextDouble(cfg.blinkIntervalMin, cfg.blinkIntervalMax)
        }
        if (t >= nextPulseAt) {
            pulseAmplitude = cfg.pulseMaxAmplitude * motion * rand.nextDouble(0.5, 1.0)
            pulseStart = t
            nextPulseAt = t + rand.nextDouble(cfg.pulseIntervalMin, cfg.pulseIntervalMax)
        }
        if (t >= nextDriftAt) {
            // Keep a small floor so a dormant drift target can't fully stall the
            // slow layer rotation for long stretches (desktop semantics).
            driftTarget = (rand.nextDouble() * 2.0 - 1.0) * cfg.driftMaxVelocity * max(motion, 0.15)
            nextDriftAt = t + rand.nextDouble(cfg.driftIntervalMin, cfg.driftIntervalMax)
        }
        if (t >= nextWobbleAt) {
            wobbleTarget = cfg.wobbleMaxAmplitude * motion * rand.nextDouble(0.15, 1.0)
            state.wobbleHarmonics = rand.nextInt(2, 6)
            nextWobbleAt = t + rand.nextDouble(cfg.wobbleIntervalMin, cfg.wobbleIntervalMax)
        }
    }

    private fun doSaccade(motion: Double) {
        if (motion <= 0.02) {
            pupilTargetX = 0.0
            pupilTargetY = 0.0
            return
        }
        if (rand.nextDouble() < cfg.saccadeCenterChance) {
            pupilTargetX = 0.0
            pupilTargetY = 0.0
        } else {
            // sqrt for uniform disc coverage; amplitude bounded by config × motion.
            val r = cfg.saccadeMaxOffset * motion * kotlin.math.sqrt(rand.nextDouble())
            val a = rand.nextDouble() * 2.0 * PI
            pupilTargetX = r * cos(a)
            pupilTargetY = r * sin(a)
        }
    }

    private fun computeEnvelopes(): Pair<Double, Double> {
        var blinkEnv = 1.0
        if (blinkStart >= 0.0) {
            val t = state.time - blinkStart
            if (t >= cfg.blinkDuration) {
                blinkStart = -1.0
            } else {
                val phase = t / cfg.blinkDuration
                blinkEnv = 1.0 - (1.0 - cfg.blinkCloseLevel) * sin(PI * phase)
            }
        }
        var pulseNorm = 0.0
        if (pulseStart >= 0.0) {
            val t = state.time - pulseStart
            pulseNorm = exp(-t * cfg.pulseReleaseRate)
            if (pulseNorm < 0.01) {
                pulseNorm = 0.0
                pulseStart = -1.0
            }
        }
        return blinkEnv to pulseNorm
    }

    private fun clampState(s: CephalonVisualState) {
        s.eyeOpen = s.eyeOpen.coerceIn(0.0, 1.0)
        s.eyeScale = s.eyeScale.coerceIn(0.8, 1.3)
        s.eyeRotation = s.eyeRotation.coerceIn(-0.3, 0.3)
        s.pupilOffsetX = s.pupilOffsetX.coerceIn(-1.0, 1.0)
        s.pupilOffsetY = s.pupilOffsetY.coerceIn(-1.0, 1.0)
        s.pupilScale = s.pupilScale.coerceIn(0.6, 1.6)
        s.shellExpansion = s.shellExpansion.coerceIn(0.75, 1.55)
        s.wobbleAmp = s.wobbleAmp.coerceIn(0.0, 0.06)
        s.glow = s.glow.coerceIn(0.0, 1.0)
        s.particleEnergy = s.particleEnergy.coerceIn(0.0, 1.0)
        s.spectrumShimmer = s.spectrumShimmer.coerceIn(0.0, 1.0)
        s.tilt = s.tilt.coerceIn(-0.05, 0.05)
        s.shellRotation = wrap(s.shellRotation)
        s.shell2Rotation = wrap(s.shell2Rotation)
        s.bracketRotation = wrap(s.bracketRotation)
        s.irisRotation = wrap(s.irisRotation)
        if (!s.time.isFinite()) s.time = 0.0
    }

    private fun ease(current: Double, target: Double, rate: Double, dt: Double): Double {
        val k = 1.0 - exp(-rate * dt)
        return current + (target - current) * k
    }

    private fun wrap(angle: Double): Double {
        if (!angle.isFinite()) return 0.0
        var a = angle % (2.0 * PI)
        if (a > PI) a -= 2.0 * PI
        if (a <= -PI) a += 2.0 * PI
        return a
    }
}
