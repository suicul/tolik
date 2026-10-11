package com.tolik.client

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import kotlin.random.Random

// Mode → animator/palette mapping (AN-P2, requirement: all 7 modes): every
// Core mode lands on its tone row, RED is reserved for real Alert/Locked
// events (fail-closed — never a renderer-invented alarm), and the animator's
// eased state honors each mode's render policy. Pure JVM, no Android types.
class CephalonModeMappingTest {

    // ── Mode → tone (the color vocabulary) ──────────────────────────────────

    @Test
    fun `every wire mode maps to its tone row`() {
        val expected = mapOf(
            ExpressionMode.Idle to CephalonTone.Calm,
            ExpressionMode.Thinking to CephalonTone.Working,
            ExpressionMode.Speaking to CephalonTone.Voice,
            ExpressionMode.Working to CephalonTone.Working,
            ExpressionMode.Recovering to CephalonTone.Recovering,
            ExpressionMode.Alert to CephalonTone.Alarm,
            ExpressionMode.Locked to CephalonTone.Locked,
        )
        assertEquals(
            "all seven modes must be mapped (fail-closed lookup must never trigger)",
            expected.keys, ExpressionMode.entries.toSet(),
        )
        for ((mode, tone) in expected) {
            assertEquals("tone of $mode", tone, CephalonPalette.toneFor(mode))
        }
    }

    @Test
    fun `alarm red is resolved only from Alert and Locked`() {
        val alarmRedHex = hexOf(CephalonPalette.AlarmRed)
        for (mode in ExpressionMode.entries) {
            val colorHex = hexOf(CephalonPalette.resolve(CephalonPalette.toneFor(mode)))
            val shouldBeRed = mode == ExpressionMode.Alert || mode == ExpressionMode.Locked
            assertEquals(
                "red leaks into $mode (${CephalonPalette.toneFor(mode)})",
                shouldBeRed,
                colorHex == alarmRedHex,
            )
        }
    }

    @Test
    fun `palette hex values match the reference vocabulary`() {
        // Same hex vocabulary as the desktop CephalonPalette / cephalon.html.
        assertEquals("FF0C1322", hexOf(CephalonPalette.ShellBase))
        assertEquals("FF1B2C46", hexOf(CephalonPalette.ShellEdge))
        assertEquals("FF58A6FF", hexOf(CephalonPalette.EdgeBlue))
        assertEquals("FF34507A", hexOf(CephalonPalette.EdgeDim))
        assertEquals("FFA8D4FF", hexOf(CephalonPalette.IrisBright))
        assertEquals("FF1C3D6E", hexOf(CephalonPalette.IrisDeep))
        assertEquals("FF04060C", hexOf(CephalonPalette.PupilDark))
        assertEquals("FF3FB950", hexOf(CephalonPalette.VoiceGreen))
        assertEquals("FFF85149", hexOf(CephalonPalette.AlarmRed))
        assertEquals("FF8FA6C4", hexOf(CephalonPalette.TextDim))
    }

    @Test
    fun `mode to resolved color is the full seven-row table`() {
        val expected = mapOf(
            ExpressionMode.Idle to "FF58A6FF",       // calm blue
            ExpressionMode.Thinking to "FF58A6FF",   // working blue
            ExpressionMode.Working to "FF58A6FF",   // working blue
            ExpressionMode.Speaking to "FF3FB950",  // voice green
            ExpressionMode.Recovering to "FF34507A", // dim structural blue
            ExpressionMode.Alert to "FFF85149",     // alarm red
            ExpressionMode.Locked to "FFF85149",     // lock red
        )
        for ((mode, hex) in expected) {
            assertEquals("color of $mode", hex, hexOf(CephalonPalette.resolve(CephalonPalette.toneFor(mode))))
        }
    }

    // ── Mode → render policy (shape; color is never the only carrier) ──────

    @Test
    fun `locked policy caps eye motion and particles`() {
        val adjusted = CephalonModeRenderPolicy.applyToTargets(
            mode = ExpressionMode.Locked,
            eyeOpen = 0.95, motion = 0.9, particles = 0.8, glow = 0.5,
        )
        assertEquals(CephalonModeRenderPolicy.LOCKED_EYE_OPEN_CEILING, adjusted.eyeOpen, 1e-12)
        assertEquals(CephalonModeRenderPolicy.LOCKED_MOTION_CEILING, adjusted.motion, 1e-12)
        assertEquals(CephalonModeRenderPolicy.LOCKED_PARTICLE_CEILING, adjusted.particles, 1e-12)
        assertEquals(0.5, adjusted.glow, 1e-12) // glow passes through untouched
    }

    @Test
    fun `alert policy lifts the eye and glow floors`() {
        val adjusted = CephalonModeRenderPolicy.applyToTargets(
            mode = ExpressionMode.Alert,
            eyeOpen = 0.1, motion = 0.2, particles = 0.3, glow = 0.05,
        )
        assertEquals(CephalonModeRenderPolicy.ALERT_EYE_OPEN_FLOOR, adjusted.eyeOpen, 1e-12)
        assertEquals(CephalonModeRenderPolicy.ALERT_GLOW_FLOOR, adjusted.glow, 1e-12)
        assertEquals(0.2, adjusted.motion, 1e-12)   // motion passes through
        assertEquals(0.3, adjusted.particles, 1e-12) // particles pass through
    }

    @Test
    fun `non-alarm modes pass every target through untouched`() {
        val calm = ExpressionMode.entries - setOf(ExpressionMode.Alert, ExpressionMode.Locked)
        for (mode in calm) {
            val adjusted = CephalonModeRenderPolicy.applyToTargets(
                mode = mode,
                eyeOpen = 0.42, motion = 0.71, particles = 0.63, glow = 0.34,
            )
            assertEquals("eyeOpen passthrough for $mode", 0.42, adjusted.eyeOpen, 1e-12)
            assertEquals("motion passthrough for $mode", 0.71, adjusted.motion, 1e-12)
            assertEquals("particles passthrough for $mode", 0.63, adjusted.particles, 1e-12)
            assertEquals("glow passthrough for $mode", 0.34, adjusted.glow, 1e-12)
        }
    }

    // ── End-to-end: the animator honors each of the seven modes ────────────

    @Test
    fun `animator applies each of the seven modes within its policy`() {
        val dt = 1.0 / 60.0
        for (mode in ExpressionMode.entries) {
            val animator = CephalonAnimator(random = Random(0xCECEL + mode.ordinal))
            animator.setExpression(
                ExpressionOutput.NEUTRAL.copy(
                    mode = mode,
                    eyeOpenness = 0.2,    // low: the Alert floor must lift it
                    glowIntensity = 0.0,  // low: the Alert floor must lift it
                    particleDensity = 1.0, // high: the Locked cap must drop it
                    movementSpeed = 1.0,
                ),
            )
            repeat((3.0 / dt).toInt()) { animator.update(dt) }

            assertEquals("animator mode target", mode, animator.mode)
            assertEquals("animator tone", CephalonPalette.toneFor(mode), animator.tone)
            val s = animator.state
            // Global sanity per mode: bounded and finite.
            assertTrue("eyeOpen of $mode", s.eyeOpen.isFinite() && s.eyeOpen in 0.0..1.0)
            assertTrue("glow of $mode", s.glow.isFinite() && s.glow in 0.0..1.0)
            assertTrue("particles of $mode", s.particleEnergy.isFinite() && s.particleEnergy in 0.0..1.0)

            when (mode) {
                ExpressionMode.Locked -> {
                    assertTrue(
                        "locked eyeOpen must cap at the ceiling",
                        s.eyeOpen <= CephalonModeRenderPolicy.LOCKED_EYE_OPEN_CEILING + 0.01,
                    )
                    assertTrue(
                        "locked particles must cap at the ceiling",
                        s.particleEnergy <= CephalonModeRenderPolicy.LOCKED_PARTICLE_CEILING + 0.01,
                    )
                }
                ExpressionMode.Alert -> {
                    assertTrue(
                        "alert eyeOpen must reach the floor",
                        s.eyeOpen >= CephalonModeRenderPolicy.ALERT_EYE_OPEN_FLOOR - 0.01,
                    )
                    assertTrue(
                        "alert glow must reach the floor",
                        s.glow >= CephalonModeRenderPolicy.ALERT_GLOW_FLOOR - 0.01,
                    )
                }
                else -> {
                    // No alarm policy: the shape follows the raw Core fields —
                    // no floor lifts the low eye/glow, no cap cuts the dense
                    // particles (this is exactly what distinguishes the calm
                    // modes from Alert/Locked behaviorally).
                    assertTrue("no eye floor for $mode", s.eyeOpen <= 0.21)
                    assertTrue("no glow floor for $mode", s.glow < 0.55)
                    assertTrue("no particle cap for $mode", s.particleEnergy >= 0.7)
                }
            }
        }
    }

    private fun hexOf(color: Int): String = "%08X".format(color)
}
