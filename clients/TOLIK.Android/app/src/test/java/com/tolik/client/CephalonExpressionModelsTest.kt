package com.tolik.client

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import kotlin.math.PI
import org.junit.Test

// Normalization of the Kotlin mirror of TOLIK.Contracts.ExpressionOutput
// (AN-P2): every double clamps to its documented bound; NaN/±Infinity collapse
// to that field's neutral default — garbage never renders as a spike.
class CephalonExpressionModelsTest {

    private val n = ExpressionOutput.NEUTRAL

    /** Builds a cephalon_expression frame with the given raw JSON fields and
     * parses it — exercises the same path the transport uses. */
    private fun parseWith(fieldsJson: String): ExpressionOutput {
        val frame =
            """{"type":"event","payload":{"kind":"cephalon_expression",""" +
                """"expression":{$fieldsJson},"at":"2026-10-11T00:00:00Z"}}"""
        return requireNotNull(CephalonWireParser.parseFrame(frame))
    }

    private fun normalizedOf(name: String, value: Double): ExpressionOutput =
        parseWith("\"$name\":$value")

    @Test
    fun `neutral is preserved by normalization`() {
        assertEquals(n, n.normalized())
    }

    @Test
    fun `eyeScale clamps to 0_8 - 1_3`() {
        assertEquals(0.8, normalizedOf("eyeScale", 0.1).eyeScale, 1e-9)
        assertEquals(1.3, normalizedOf("eyeScale", 9.0).eyeScale, 1e-9)
        assertEquals(1.0, normalizedOf("eyeScale", 1.0).eyeScale, 1e-9)
    }

    @Test
    fun `eyeRotation clamps to -0_3 - 0_3`() {
        assertEquals(-0.3, normalizedOf("eyeRotation", -5.0).eyeRotation, 1e-9)
        assertEquals(0.3, normalizedOf("eyeRotation", 5.0).eyeRotation, 1e-9)
    }

    @Test
    fun `shellExpansion clamps to -0_2 - 0_5`() {
        assertEquals(-0.2, normalizedOf("shellExpansion", -3.0).shellExpansion, 1e-9)
        assertEquals(0.5, normalizedOf("shellExpansion", 3.0).shellExpansion, 1e-9)
    }

    @Test
    fun `rotation clamps to -0_5 - 0_5`() {
        assertEquals(-0.5, normalizedOf("rotation", -2.0).rotation, 1e-9)
        assertEquals(0.5, normalizedOf("rotation", 2.0).rotation, 1e-9)
    }

    @Test
    fun `attentionAngle clamps to -pi - pi`() {
        assertEquals(-PI, normalizedOf("attentionAngle", -10.0).attentionAngle, 1e-9)
        assertEquals(PI, normalizedOf("attentionAngle", 10.0).attentionAngle, 1e-9)
        assertEquals(1.5, normalizedOf("attentionAngle", 1.5).attentionAngle, 1e-9)
    }

    @Test
    fun `unit-range fields clamp to 0-1`() {
        for (name in listOf(
            "eyeOpenness", "asymmetry", "shellDeformation", "pulseAmplitude",
            "glowIntensity", "particleDensity", "spectrumIntensity",
            "movementSpeed", "attentionIntensity", "transitionEnergy",
        )) {
            val low = normalizedOf(name, -1.0)
            val high = normalizedOf(name, 7.0)
            assertEquals("lower clamp of $name", 0.0, low.readField(name), 1e-9)
            assertEquals("upper clamp of $name", 1.0, high.readField(name), 1e-9)
        }
    }

    @Test
    fun `non-finite values collapse to neutral defaults`() {
        for (garbage in listOf(Double.NaN, Double.POSITIVE_INFINITY, Double.NEGATIVE_INFINITY)) {
            val out = ExpressionOutput(
                eyeScale = garbage, eyeOpenness = garbage, eyeRotation = garbage,
                asymmetry = garbage, shellExpansion = garbage, shellDeformation = garbage,
                rotation = garbage, pulseAmplitude = garbage, glowIntensity = garbage,
                particleDensity = garbage, spectrumIntensity = garbage,
                movementSpeed = garbage, attentionAngle = garbage,
                attentionIntensity = garbage, transitionEnergy = garbage,
            ).normalized()
            assertEquals(n, out)
        }
        // Sanity: a finite pass-through differs (the test above isn't vacuous).
        val finite = n.copy(glowIntensity = 0.9).normalized()
        assertTrue(finite != n)
    }

    @Test
    fun `mode maps all seven wire values`() {
        for (mode in ExpressionMode.entries) {
            val out = parseWith("\"mode\":\"${mode.wire}\"")
            assertEquals(mode, out.mode)
        }
    }

    @Test
    fun `unknown mode fails closed to Idle`() {
        assertEquals(ExpressionMode.Idle, parseWith("\"mode\":\"Ecstatic\"").mode)
        // Missing mode entirely → Idle.
        val frame = """{"type":"event","payload":{"kind":"cephalon_expression",
            "expression":{}}}""".replace("\n", "")
        assertEquals(ExpressionMode.Idle, CephalonWireParser.parseFrame(frame)?.mode)
    }

    @Test
    fun `all output doubles stay within documented bounds`() {
        val wild = ExpressionOutput(
            eyeScale = 42.0, eyeOpenness = -9.0, eyeRotation = 100.0, asymmetry = 3.5,
            shellExpansion = -19.0, shellDeformation = 2.0, rotation = 77.0,
            pulseAmplitude = 5.0, glowIntensity = -1.5, particleDensity = 12.0,
            spectrumIntensity = 0.5, movementSpeed = 3.0, attentionAngle = 99.0,
            attentionIntensity = 4.0, transitionEnergy = -4.0, mode = ExpressionMode.Thinking,
        ).normalized()
        assertTrue(wild.eyeScale in 0.8..1.3)
        assertTrue(wild.eyeOpenness in 0.0..1.0)
        assertTrue(wild.eyeRotation in -0.3..0.3)
        assertTrue(wild.asymmetry in 0.0..1.0)
        assertTrue(wild.shellExpansion in -0.2..0.5)
        assertTrue(wild.shellDeformation in 0.0..1.0)
        assertTrue(wild.rotation in -0.5..0.5)
        assertTrue(wild.pulseAmplitude in 0.0..1.0)
        assertTrue(wild.glowIntensity in 0.0..1.0)
        assertTrue(wild.particleDensity in 0.0..1.0)
        assertTrue(wild.spectrumIntensity in 0.0..1.0)
        assertTrue(wild.movementSpeed in 0.0..1.0)
        assertTrue(wild.attentionAngle in -PI..PI)
        assertTrue(wild.attentionIntensity in 0.0..1.0)
        assertTrue(wild.transitionEnergy in 0.0..1.0)
    }

    /** Reads a field by wire name for table-driven assertions. */
    private fun ExpressionOutput.readField(name: String): Double = when (name) {
        "eyeScale" -> eyeScale
        "eyeOpenness" -> eyeOpenness
        "eyeRotation" -> eyeRotation
        "asymmetry" -> asymmetry
        "shellExpansion" -> shellExpansion
        "shellDeformation" -> shellDeformation
        "rotation" -> rotation
        "pulseAmplitude" -> pulseAmplitude
        "glowIntensity" -> glowIntensity
        "particleDensity" -> particleDensity
        "spectrumIntensity" -> spectrumIntensity
        "movementSpeed" -> movementSpeed
        "attentionAngle" -> attentionAngle
        "attentionIntensity" -> attentionIntensity
        "transitionEnergy" -> transitionEnergy
        else -> error("unknown field $name")
    }
}
