package com.tolik.client

import kotlin.math.PI

// Kotlin mirror of the WIRE model — TOLIK.Contracts.ExpressionOutput
// (src/TOLIK.Contracts/ExpressionState.cs), consumed over /events/ws as
// {"type":"event","payload":{"kind":"cephalon_expression","expression":{…},"at":…}}.
//
// ONE COMPOSER, MANY RENDERERS (docs/android-app.md §3): Core composes the
// expression; this class only mirrors the JSON contract and normalizes it.
// There is NO composer port here and MUST NOT be one — the mapping from
// ExpressionState to ExpressionOutput stays Core-owned.
//
// Normalization rule (mirrors ExpressionOutput.Clamped + fail-closed §18):
// every double clamps to its documented bound; a non-finite value collapses
// to that field's neutral default — garbage never renders as a spike.

/** Dominant visual mode hint from Core. The renderer maps it to a color
 * vocabulary (palette); SHAPE parameters arrive in the fields, not here.
 * Unknown wire strings fail closed to [Idle] — never a fake alarm. */
enum class ExpressionMode(val wire: String) {
    Idle("Idle"),
    Thinking("Thinking"),
    Speaking("Speaking"),
    Working("Working"),
    Recovering("Recovering"),
    Alert("Alert"),
    Locked("Locked");

    companion object {
        /** Strict wire-name lookup; anything unknown → Idle (calm blue, §14). */
        fun fromWire(raw: String?): ExpressionMode =
            entries.firstOrNull { it.wire == raw } ?: Idle
    }
}

/** The composed visual-behavior parameters Core broadcasts (~5 Hz on change).
 * Field order and defaults mirror the C# record exactly. */
data class ExpressionOutput(
    val eyeScale: Double,           // [0.8..1.3]
    val eyeOpenness: Double,        // [0..1]
    val eyeRotation: Double,        // [-0.3..0.3] rad
    val asymmetry: Double,          // [0..1]
    val shellExpansion: Double,     // [-0.2..0.5]
    val shellDeformation: Double,   // [0..1]
    val rotation: Double,           // [-0.5..0.5] rad/s drift
    val pulseAmplitude: Double,     // [0..1]
    val glowIntensity: Double,      // [0..1]
    val particleDensity: Double,    // [0..1]
    val spectrumIntensity: Double,  // [0..1]
    val movementSpeed: Double,      // [0..1]
    val attentionAngle: Double,     // [-PI..PI] rad
    val attentionIntensity: Double, // [0..1]
    val transitionEnergy: Double,   // [0..1]
    val mode: ExpressionMode = ExpressionMode.Idle,
) {
    /** The honest default: quiet idle, no signals (mirrors ExpressionOutput.Neutral). */
    companion object {
        const val EYE_SCALE_MIN = 0.8
        const val EYE_SCALE_MAX = 1.3
        const val EYE_ROTATION_MAX = 0.3
        const val SHELL_EXPANSION_MIN = -0.2
        const val SHELL_EXPANSION_MAX = 0.5
        const val ROTATION_MAX = 0.5
        const val ATTENTION_ANGLE_MAX = PI

        val NEUTRAL: ExpressionOutput = ExpressionOutput(
            eyeScale = 1.0,
            eyeOpenness = 0.75,
            eyeRotation = 0.0,
            asymmetry = 0.0,
            shellExpansion = 0.0,
            shellDeformation = 0.0,
            rotation = 0.0,
            pulseAmplitude = 0.08,
            glowIntensity = 0.25,
            particleDensity = 0.1,
            spectrumIntensity = 0.0,
            movementSpeed = 0.15,
            attentionAngle = 0.0,
            attentionIntensity = 0.0,
            transitionEnergy = 0.3,
            mode = ExpressionMode.Idle,
        )
    }

    /** Every double clamped to its documented range; non-finite → this field's
     * neutral default (fail closed to neutral, never a spike). */
    fun normalized(): ExpressionOutput {
        val n = NEUTRAL
        return ExpressionOutput(
            eyeScale = clampBounded(eyeScale, EYE_SCALE_MIN, EYE_SCALE_MAX, n.eyeScale),
            eyeOpenness = clampNeutral01(eyeOpenness, n.eyeOpenness),
            eyeRotation = clampSymmetric(eyeRotation, EYE_ROTATION_MAX, n.eyeRotation),
            asymmetry = clampNeutral01(asymmetry, n.asymmetry),
            shellExpansion = clampBounded(
                shellExpansion, SHELL_EXPANSION_MIN, SHELL_EXPANSION_MAX, n.shellExpansion,
            ),
            shellDeformation = clampNeutral01(shellDeformation, n.shellDeformation),
            rotation = clampSymmetric(rotation, ROTATION_MAX, n.rotation),
            pulseAmplitude = clampNeutral01(pulseAmplitude, n.pulseAmplitude),
            glowIntensity = clampNeutral01(glowIntensity, n.glowIntensity),
            particleDensity = clampNeutral01(particleDensity, n.particleDensity),
            spectrumIntensity = clampNeutral01(spectrumIntensity, n.spectrumIntensity),
            movementSpeed = clampNeutral01(movementSpeed, n.movementSpeed),
            attentionAngle = clampSymmetric(attentionAngle, ATTENTION_ANGLE_MAX, n.attentionAngle),
            attentionIntensity = clampNeutral01(attentionIntensity, n.attentionIntensity),
            transitionEnergy = clampNeutral01(transitionEnergy, n.transitionEnergy),
            mode = mode,
        )
    }

    /** [0..1] bound; non-finite → this field's neutral default. */
    private fun clampNeutral01(v: Double, neutral: Double): Double =
        clampBounded(v, 0.0, 1.0, neutral)

    /** Symmetric [-max..max] bound; non-finite → fallback. */
    private fun clampSymmetric(v: Double, max: Double, fallback: Double): Double =
        clampBounded(v, -max, max, fallback)

    private fun clampBounded(v: Double, min: Double, max: Double, fallback: Double): Double =
        if (v.isFinite()) v.coerceIn(min, max) else fallback
}
