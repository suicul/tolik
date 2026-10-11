package com.tolik.client

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import kotlin.math.abs
import kotlin.math.PI
import kotlin.random.Random

// Bounded procedural idle (AN-P2): with a fixed seed the body is deterministic,
// and every stochastic action stays inside IdleMotionConfig bounds — alive but
// never erratic, never an infinite noticeable loop (desktop IdleMotionConfig
// semantics, mobile amplitudes).
class CephalonIdleBoundsTest {

    private val dt = 1.0 / 60.0
    private val durationSec = 180.0

    private fun runAnimator(seed: Long, steps: Int): CephalonAnimator {
        val animator = CephalonAnimator(random = Random(seed))
        repeat(steps) { animator.update(dt) }
        return animator
    }

    @Test
    fun `same seed produces an identical trajectory`() {
        val a = runAnimator(seed = 42L, steps = (durationSec / dt).toInt())
        val b = runAnimator(seed = 42L, steps = (durationSec / dt).toInt())
        assertEquals(a.state.time, b.state.time, 1e-12)
        assertEquals(a.state.eyeOpen, b.state.eyeOpen, 1e-12)
        assertEquals(a.state.pupilOffsetX, b.state.pupilOffsetX, 1e-12)
        assertEquals(a.state.shellRotation, b.state.shellRotation, 1e-12)
        assertEquals(a.state.glow, b.state.glow, 1e-12)
    }

    @Test
    fun `different seeds diverge (not a fixed loop)`() {
        val a = runAnimator(seed = 1L, steps = 600)
        val b = runAnimator(seed = 2L, steps = 600)
        val different = a.state.pupilOffsetX != b.state.pupilOffsetX ||
            a.state.shellRotation != b.state.shellRotation ||
            a.state.glow != b.state.glow
        assertTrue("seeded trajectories must differ", different)
    }

    @Test
    fun `all visual state stays finite and bounded over 3 minutes`() {
        val animator = runAnimator(seed = 7L, steps = (durationSec / dt).toInt())
        val s = animator.state
        assertTrue(s.eyeOpen.isFinite() && s.eyeOpen in 0.0..1.0)
        assertTrue(s.eyeScale.isFinite() && s.eyeScale in 0.8..1.3)
        assertTrue(s.eyeRotation.isFinite() && s.eyeRotation in -0.3..0.3)
        assertTrue(s.pupilOffsetX.isFinite() && s.pupilOffsetX in -1.0..1.0)
        assertTrue(s.pupilOffsetY.isFinite() && s.pupilOffsetY in -1.0..1.0)
        assertTrue(s.pupilScale.isFinite() && s.pupilScale in 0.6..1.6)
        assertTrue(s.shellExpansion.isFinite() && s.shellExpansion in 0.9..1.1)
        assertTrue(s.wobbleAmp.isFinite() && s.wobbleAmp >= 0.0)
        assertTrue(s.glow.isFinite() && s.glow in 0.0..1.0)
        assertTrue(s.particleEnergy.isFinite() && s.particleEnergy in 0.0..1.0)
        assertTrue(s.tilt.isFinite() && abs(s.tilt) <= 0.05)
        assertTrue(s.time.isFinite())
        for (angle in listOf(
            s.shellRotation, s.shell2Rotation, s.bracketRotation, s.irisRotation,
        )) {
            assertTrue(angle.isFinite() && angle in -PI..PI)
        }
    }

    @Test
    fun `saccade offsets stay within the configured max`() {
        val animator = runAnimator(seed = 11L, steps = (durationSec / dt).toInt())
        val maxOffset = animator.config.saccadeMaxOffset + 1e-9
        // Neutral expression: attentionIntensity = 0, so pupil offset IS the
        // saccade offset. Sample over a second run while recording the peak.
        var peak = 0.0
        repeat((durationSec / dt).toInt()) {
            animator.update(dt)
            peak = maxOf(peak, abs(animator.state.pupilOffsetX), abs(animator.state.pupilOffsetY))
        }
        assertTrue(
            "saccade peak $peak exceeded $maxOffset",
            peak <= maxOffset,
        )
    }

    @Test
    fun `blink intervals stay within the configured bounds`() {
        val cfg = IdleMotionConfig.Mobile
        val animator = CephalonAnimator(random = Random(21L))
        var lastBlinkAt = -1.0
        val intervals = mutableListOf<Double>()
        val steps = (durationSec / dt).toInt()
        repeat(steps) {
            animator.update(dt)
            // A blink is visible as a deep eyeOpen dip below the 0.75 baseline.
            if (animator.state.eyeOpen < 0.55) {
                if (lastBlinkAt >= 0.0) {
                    val interval = animator.state.time - lastBlinkAt
                    // Detection lag: only count once per blink (dips last ~0.35 s).
                    if (interval > 1.0) intervals.add(interval)
                }
                lastBlinkAt = animator.state.time
            }
        }
        assertTrue("expected several blinks, got ${intervals.size}", intervals.size >= 3)
        for (interval in intervals) {
            assertTrue(
                "blink interval $interval outside [${cfg.blinkIntervalMin}, ${cfg.blinkIntervalMax}] + lag",
                interval in cfg.blinkIntervalMin - 0.6..cfg.blinkIntervalMax + 0.6,
            )
        }
    }

    @Test
    fun `pulse never exceeds the configured amplitude over the baseline`() {
        val cfg = IdleMotionConfig.Mobile
        val animator = CephalonAnimator(random = Random(33L))
        var maxGlow = 0.0
        repeat((durationSec / dt).toInt()) {
            animator.update(dt)
            maxGlow = maxOf(maxGlow, animator.state.glow)
        }
        // Neutral glow baseline 0.25 + bounded pulse bump.
        val ceiling = ExpressionOutput.NEUTRAL.glowIntensity + cfg.pulseMaxAmplitude + 1e-9
        assertTrue("glow peak $maxGlow exceeded $ceiling", maxGlow <= ceiling)
    }

    @Test
    fun `shell layer rotation speed stays within drift bounds`() {
        val cfg = IdleMotionConfig.Mobile
        val animator = CephalonAnimator(random = Random(55L))
        var lastRotation = animator.state.shellRotation
        var maxDelta = 0.0
        repeat((durationSec / dt).toInt()) {
            animator.update(dt)
            // Wrap-aware delta.
            var delta = animator.state.shellRotation - lastRotation
            if (delta > PI) delta -= 2 * PI
            if (delta < -PI) delta += 2 * PI
            maxDelta = maxOf(maxDelta, abs(delta) / dt)
            lastRotation = animator.state.shellRotation
        }
        // baseOuterOmega × |drift ≤ DriftMaxVelocity| (neutral: no Core rotation).
        val ceiling = cfg.baseOuterOmega * cfg.driftMaxVelocity + 1e-9
        assertTrue("rotation speed $maxDelta rad/s exceeded $ceiling", maxDelta <= ceiling)
    }

    @Test
    fun `locked mode renders fail-closed shape ceilings`() {
        val animator = CephalonAnimator(random = Random(77L))
        animator.setExpression(
            ExpressionOutput.NEUTRAL.copy(
                mode = ExpressionMode.Locked,
                glowIntensity = 0.9,
                particleDensity = 0.9, // far above the locked ceiling — proves the cap
            ),
        )
        // Ease for 3 s.
        repeat((3.0 / dt).toInt()) { animator.update(dt) }
        assertTrue(
            "locked eyeOpen must stay ≤ ceiling",
            animator.state.eyeOpen <= CephalonModeRenderPolicy.LOCKED_EYE_OPEN_CEILING + 0.01,
        )
        assertTrue(
            "locked particles must stay ≤ ceiling",
            animator.state.particleEnergy <= CephalonModeRenderPolicy.LOCKED_PARTICLE_CEILING + 0.01,
        )
        // Color is NOT the only carrier: the shape really changed.
        assertTrue(animator.state.eyeOpen < ExpressionOutput.NEUTRAL.eyeOpenness)
    }

    @Test
    fun `alert mode lifts glow floor without waiting for shape fields`() {
        val animator = CephalonAnimator(random = Random(99L))
        animator.setExpression(
            ExpressionOutput.NEUTRAL.copy(mode = ExpressionMode.Alert, glowIntensity = 0.0),
        )
        repeat((3.0 / dt).toInt()) { animator.update(dt) }
        assertTrue(
            "alert glow must reach the floor (color never the only carrier)",
            animator.state.glow >= CephalonModeRenderPolicy.ALERT_GLOW_FLOOR - 0.01,
        )
    }

    @Test
    fun `easing converges toward a new target without snapping`() {
        val animator = CephalonAnimator(random = Random(123L))
        // From neutral toward a very different target.
        animator.setExpression(ExpressionOutput.NEUTRAL.copy(glowIntensity = 1.0))
        // One step: the value must MOVE but must NOT land on the target.
        val before = animator.state.glow
        animator.update(dt)
        val afterOneStep = animator.state.glow
        assertTrue(afterOneStep > before + 1e-6)
        assertTrue(afterOneStep < 1.0 - 1e-3)
        // After 4 s it is (almost) there.
        repeat((4.0 / dt).toInt()) { animator.update(dt) }
        assertTrue(animator.state.glow > 0.99)
    }
}
