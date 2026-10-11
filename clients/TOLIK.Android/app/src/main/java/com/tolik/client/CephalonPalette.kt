package com.tolik.client

// Color system of the mobile Cephalon — mirrors src/TOLIK.Desktop/Rendering/
// CephalonPalette.cs (same hex vocabulary, interface/cephalon.html palette family).
//
// BLUE is the primary family. RED is defined but ONLY mapped from the
// Alert/Locked expression modes — real Core events (SECURITY §26: the renderer
// never invents an alarm; idle/fabricated states stay calm blue). Color is
// never the only carrier: the shape behavior for alarm modes changes too
// (CephalonModeRenderPolicy below).
//
// Pure Kotlin (no android.graphics): the palette is DATA, and data must be
// testable in plain JVM unit tests (docs/android-app.md §9).

enum class CephalonTone {
    /** Calm blue — idle presence. */
    Calm,

    /** Working blue — brighter, denser activity (thinking/working). */
    Working,

    /** Voice green accent — speaking. */
    Voice,

    /** Recovering — dim structural blue (reduced, cautious). */
    Recovering,

    /** Alarm red — real Alert events only. */
    Alarm,

    /** Dull red — security lock. Fail-closed rendered visually. */
    Locked,
}

object CephalonPalette {
    val ShellBase: Int = parse("#0C1322")   // eye backing / membranes
    val ShellEdge: Int = parse("#1B2C46")   // dim structural edges
    val EdgeBlue: Int = parse("#58A6FF")    // primary accent (reference vocabulary)
    val EdgeDim: Int = parse("#34507A")     // secondary structural blue
    val IrisBright: Int = parse("#A8D4FF")  // iris core
    val IrisDeep: Int = parse("#1C3D6E")    // iris rim
    val PupilDark: Int = parse("#04060C")
    val VoiceGreen: Int = parse("#3FB950")  // reserved: speaking tone
    val AlarmRed: Int = parse("#F85149")    // reserved: Alert/Locked tones ONLY
    val TextDim: Int = parse("#8FA6C4")

    /** (iris fill, accent line) per tone. Unmapped tones fail closed to Calm blue. */
    fun resolve(tone: CephalonTone): Int = when (tone) {
        CephalonTone.Working -> EdgeBlue
        CephalonTone.Voice -> VoiceGreen
        CephalonTone.Alarm -> AlarmRed
        CephalonTone.Locked -> AlarmRed
        CephalonTone.Recovering -> EdgeDim
        CephalonTone.Calm -> EdgeBlue
    }

    /** Mode → tone. Unmapped/unknown modes fail closed to Calm (never fake red). */
    fun toneFor(mode: ExpressionMode): CephalonTone = when (mode) {
        ExpressionMode.Thinking -> CephalonTone.Working
        ExpressionMode.Working -> CephalonTone.Working
        ExpressionMode.Speaking -> CephalonTone.Voice
        ExpressionMode.Recovering -> CephalonTone.Recovering
        ExpressionMode.Alert -> CephalonTone.Alarm
        ExpressionMode.Locked -> CephalonTone.Locked
        ExpressionMode.Idle -> CephalonTone.Calm
    }

    /** #RRGGBB → opaque ARGB int — identical output to android.graphics.Color
     * .parseColor, without the framework dependency (JVM-testable). */
    internal fun parse(hex: String): Int {
        val digits = if (hex.startsWith("#")) hex.substring(1) else hex
        require(digits.length == 6) { "expected #RRGGBB, got: $hex" }
        fun channel(index: Int): Int {
            val high = Character.digit(digits[index], 16)
            val low = Character.digit(digits[index + 1], 16)
            require(high in 0..15 && low in 0..15) { "bad hex digit in: $hex" }
            return (high shl 4) or low
        }
        val r = channel(0)
        val g = channel(2)
        val b = channel(4)
        return ((0xFF shl 24) or (r shl 16)) or ((g shl 8) or b)
    }
}

/**
 * Renderer-side interpretation of the two alarm modes (bounded, deterministic).
 *
 * Per contract, Mode is a color-vocabulary hint and SHAPE comes from the Core
 * fields. This policy adds ONLY fail-closed ceilings/floors for Alert/Locked so
 * that a real alarm never renders as calm-idle even if the shape fields lag:
 * color is never the only carrier (директива §2, desktop StateTraits semantics).
 * It is rendering policy on a REAL event — not a composer: no state is invented,
 * accumulated, or prioritized here.
 */
object CephalonModeRenderPolicy {
    const val LOCKED_EYE_OPEN_CEILING = 0.30   // desktop Locked trait eye 0.18..0.5 band
    const val LOCKED_MOTION_CEILING = 0.35     // desktop Locked motion 0.3
    const val LOCKED_PARTICLE_CEILING = 0.15   // desktop Locked particles 0.15

    const val ALERT_EYE_OPEN_FLOOR = 0.55      // alert = wide awake
    const val ALERT_GLOW_FLOOR = 0.55          // alert = visibly lit

    fun applyToTargets(
        mode: ExpressionMode,
        eyeOpen: Double,
        motion: Double,
        particles: Double,
        glow: Double,
    ): AlarmAdjusted = when (mode) {
        ExpressionMode.Locked -> AlarmAdjusted(
            eyeOpen = minOf(eyeOpen, LOCKED_EYE_OPEN_CEILING),
            motion = minOf(motion, LOCKED_MOTION_CEILING),
            particles = minOf(particles, LOCKED_PARTICLE_CEILING),
            glow = glow,
        )
        ExpressionMode.Alert -> AlarmAdjusted(
            eyeOpen = maxOf(eyeOpen, ALERT_EYE_OPEN_FLOOR),
            motion = motion,
            particles = particles,
            glow = maxOf(glow, ALERT_GLOW_FLOOR),
        )
        else -> AlarmAdjusted(eyeOpen, motion, particles, glow)
    }

    data class AlarmAdjusted(
        val eyeOpen: Double,
        val motion: Double,
        val particles: Double,
        val glow: Double,
    )
}
