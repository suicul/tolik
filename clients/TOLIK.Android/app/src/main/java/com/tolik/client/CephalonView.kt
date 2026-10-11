package com.tolik.client

import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.Path
import android.graphics.RadialGradient
import android.graphics.RectF
import android.graphics.Shader
import android.util.AttributeSet
import android.view.SurfaceHolder
import android.view.SurfaceView
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicReference
import kotlin.math.PI
import kotlin.math.ceil
import kotlin.math.min
import kotlin.math.sin
import kotlin.random.Random

// The Cephalon renderer (AN-P2, docs/android-app.md §2/§21): a SurfaceView +
// procedural 2D Canvas (CPU) — the same visual vocabulary as the desktop
// composition (glow halo / outer ring / middle arcs / brackets / spokes / eye
// / angular iris / pupil / glint / shutter eyelids / energy particles), with
// mobile density.
//
// INVARIANTS (fail = rewrite):
// - This view contains NO identity, memory, permissions, authorization or
//   network logic. It renders what Core sends via [setExpression] plus
//   bounded procedural idle — nothing else.
// - Core events (~5 Hz) are TARGETS; the FPS loop eases toward them (the
//   animator never snaps). Without fresh events the caller must
//   [clearExpression] — the body then eases to neutral local idle, never a
//   frozen pose pretending the stream lives (desktop ClearDrive semantics).
// - Performance (§21): FPS capped (30 idle / 60 active), the render loop
//   stops when the surface is destroyed, zero per-frame allocations (all
//   Paints/Paths/buffers reused), adaptive quality on sustained slow frames.
// - Red is ONLY mapped from real Alert/Locked events (the animator's tone);
//   honest idle is calm blue.

class CephalonView @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null,
    defStyleAttr: Int = 0,
) : SurfaceView(context, attrs, defStyleAttr), SurfaceHolder.Callback {

    companion object {
        // Geometry ratios — desktop CephalonComposition vocabulary.
        private const val BASE_RADIUS_FRACTION = 0.36f
        private const val EYE_RADIUS_FRACTION = 0.52f
        private const val OUTER_RING_FRACTION = 1.06f
        private const val MIDDLE_RING_FRACTION = 0.90f
        private const val BRACKET_RING_FRACTION = 0.70f
        private const val SPOKE_INNER = 0.74f
        private const val SPOKE_OUTER = 0.84f
        private const val IRIS_FRACTION = 0.66f
        private const val PUPIL_FRACTION = 0.30f

        // Ring segment layouts (desktop: segments/span/gap in degrees).
        private const val OUTER_SEGMENTS = 6
        private const val OUTER_SPAN_DEG = 40f
        private const val OUTER_GAP_DEG = 20f
        private const val MIDDLE_SEGMENTS = 3
        private const val MIDDLE_SPAN_DEG = 78f
        private const val MIDDLE_GAP_DEG = 42f
        private const val BRACKET_SEGMENTS = 4
        private const val BRACKET_SPAN_DEG = 22f
        private const val BRACKET_GAP_DEG = 68f
        private const val IRIS_FACETS = 8

        // Fixed buffers — no per-frame allocation.
        private const val MAX_ARC_POINTS = 24
        private const val MAX_PARTICLES = 18
        private const val MAX_SPOKES = 6

        private val DEG = (PI / 180.0).toFloat()
    }

    private val running = AtomicBoolean(false)
    private val pending = AtomicReference<ExpressionOutput?>(null)
    private val animator = CephalonAnimator()
    private val governor = CephalonFrameGovernor(CephalonFrameGovernor.DEFAULT_IDLE_FPS)
    private var renderThread: Thread? = null

    /** nanoTime/1e9 of the last Core expression (drives the 30/60 fps switch). */
    @Volatile
    private var lastExpressionAtSec: Double = Double.NEGATIVE_INFINITY

    // ── reusable draw assets (built once, never per frame) ─────────────────
    private val paintGlow = Paint(Paint.ANTI_ALIAS_FLAG)
    private val paintOuter = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE; strokeCap = Paint.Cap.ROUND
    }
    private val paintMiddle = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE; strokeCap = Paint.Cap.ROUND
    }
    private val paintBracket = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE; strokeCap = Paint.Cap.ROUND
    }
    private val paintSpoke = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE; strokeCap = Paint.Cap.ROUND
    }
    private val paintEyeFill = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.FILL; color = withAlpha(CephalonPalette.ShellBase, 0.94)
    }
    private val paintScleraEdge = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE; color = withAlpha(CephalonPalette.ShellEdge, 0.85)
    }
    private val paintIris = Paint(Paint.ANTI_ALIAS_FLAG).apply { style = Paint.Style.FILL }
    private val paintFacet = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE; strokeCap = Paint.Cap.ROUND
    }
    private val paintPupil = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.FILL; color = CephalonPalette.PupilDark
    }
    private val paintGlint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.FILL; color = withAlpha(Color.WHITE, 0.4)
    }
    private val paintMembrane = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.FILL; color = withAlpha(CephalonPalette.ShellBase, 0.97)
    }
    private val paintMembraneEdge = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE
    }
    private val paintParticle = Paint(Paint.ANTI_ALIAS_FLAG).apply { style = Paint.Style.FILL }

    private val path = Path()
    private val eyeClipPath = Path()
    private val lidRect = RectF()
    private val arcBuffer = FloatArray(MAX_ARC_POINTS * 2)

    /** Deterministic seeded particle field (desktop CreateParticles semantics). */
    private val particles: Array<Particle> = buildParticles(MAX_PARTICLES, seed = 0x5EEDL)

    /** Shader caches keyed by (tone, radius bucket) — rebuilt rarely. */
    private val glowShaders = HashMap<Long, Shader>()
    private val irisShaders = HashMap<Int, Shader>()

    private class Particle(
        val angle: Float,
        val radiusFrac: Float,
        val speed: Float,
        val phase: Float,
        val sizeFrac: Float,
    )

    init {
        holder.addCallback(this)
        holder.setFormat(android.graphics.PixelFormat.TRANSLUCENT)
        setZOrderOnTop(false)
    }

    // ── public API (thread-safe) ────────────────────────────────────────────

    /** Core expression event (~5 Hz on change): parks the target for the
     * render loop. Normalization already happened at the wire boundary; this
     * normalizes again defensively (cheap, fail closed). */
    fun setExpression(expression: ExpressionOutput) {
        lastExpressionAtSec = System.nanoTime() / 1_000_000_000.0
        pending.set(expression.normalized())
    }

    /** Honest fallback (desktop CephalonAnimator.ClearDrive semantics): with no
     * fresh Core events the body eases back to the neutral procedural idle —
     * NEVER a held pose pretending the stream is still alive (директива §17/§20).
     * Idempotent; safe from any thread. */
    fun clearExpression() {
        lastExpressionAtSec = Double.NEGATIVE_INFINITY
        pending.set(ExpressionOutput.NEUTRAL)
    }

    // ── SurfaceHolder.Callback: the loop STOPS when the surface is gone ────

    override fun surfaceCreated(holder: SurfaceHolder) {
        startLoop()
    }

    override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) {
        // Radius buckets change → drop stale shaders; new ones build lazily.
        glowShaders.clear()
        irisShaders.clear()
    }

    override fun surfaceDestroyed(holder: SurfaceHolder) {
        stopLoop()
    }

    private fun startLoop() {
        if (running.getAndSet(true)) return
        renderThread = Thread({ loop() }, "cephalon-render").apply {
            priority = Thread.NORM_PRIORITY - 1
            start()
        }
    }

    private fun stopLoop() {
        running.set(false)
        val thread = renderThread
        renderThread = null
        if (thread != null && thread !== Thread.currentThread()) {
            try {
                thread.join(1000)
            } catch (_: InterruptedException) {
                Thread.currentThread().interrupt()
            }
        }
    }

    // ── render loop ─────────────────────────────────────────────────────────

    private fun loop() {
        var lastNanos = System.nanoTime()
        while (running.get()) {
            val now = System.nanoTime()
            val nowSec = now / 1_000_000_000.0
            if (!governor.shouldRender(nowSec)) {
                sleepQuiet(3)
                continue
            }
            val dt = (now - lastNanos) / 1_000_000_000.0
            lastNanos = now

            pending.getAndSet(null)?.let { animator.setExpression(it) }

            // FPS cap: 60 while the body is active (non-idle mode or a fresh
            // event), 30 in calm idle (docs/android-app.md §21).
            val active = animator.mode != ExpressionMode.Idle ||
                nowSec - lastExpressionAtSec < 2.0
            val fps = if (active) {
                CephalonFrameGovernor.DEFAULT_ACTIVE_FPS
            } else {
                CephalonFrameGovernor.DEFAULT_IDLE_FPS
            }
            if (governor.fps != fps) governor.setTargetFps(fps)

            animator.update(dt)

            val canvas: Canvas? = tryLockCanvas()
            if (canvas == null) {
                sleepQuiet(8)
                continue
            }
            try {
                // Opaque deep-space backdrop (the Cephalon palette space is
                // theme-independent — docs/android-app.md §3: light/dark system
                // themes must not alter the renderer's identity; the glow fades
                // into ShellBase). Solid fill also lets the compositor skip
                // blending behind the surface hole.
                canvas.drawColor(CephalonPalette.ShellBase)
                drawFrame(canvas, canvas.width.toFloat(), canvas.height.toFloat())
            } finally {
                try {
                    holder.unlockCanvasAndPost(canvas)
                } catch (_: Exception) {
                    // Surface died mid-frame — the loop exits on the next tick.
                }
            }
            governor.onFrameRendered((System.nanoTime() - now) / 1_000_000.0, nowSec)
        }
    }

    private fun tryLockCanvas(): Canvas? = try {
        if (holder.surface?.isValid == true) holder.lockCanvas() else null
    } catch (_: Exception) {
        null
    }

    private fun sleepQuiet(ms: Long) {
        try {
            Thread.sleep(ms)
        } catch (_: InterruptedException) {
            Thread.currentThread().interrupt()
            running.set(false)
        }
    }

    // ── frame composition (desktop CephalonComposition.Render, Canvas port) ─

    private fun drawFrame(canvas: Canvas, w: Float, h: Float) {
        val size = min(w, h)
        if (size <= 1f) return
        val cx = w / 2f
        val cy = h * 0.47f
        val r = size * BASE_RADIUS_FRACTION
        val eyeR = r * EYE_RADIUS_FRACTION
        val s = animator.state
        val tone = animator.tone
        val accent = CephalonPalette.resolve(tone)
        val quality = governor.qualityLevel
        val time = s.time.toFloat()

        // 1) inner glow halo — breathing heart of the composition. The cached
        //    shader is centered at (0,0); translate the canvas so the gradient
        //    lands on (cx, cy) — a RadialGradient applies in CANVAS coordinates,
        //    not relative to the circle being drawn.
        val glowR = r * (1.55f + 0.35f * s.glow.toFloat()) * s.shellExpansion.toFloat()
        paintGlow.shader = glowShader(tone, glowR)
        paintGlow.alpha = (255 * (0.30 + 0.55 * s.glow)).toInt().coerceIn(0, 255)
        canvas.save()
        canvas.translate(cx, cy)
        canvas.drawCircle(0f, 0f, glowR, paintGlow)
        canvas.restore()
        paintGlow.shader = null

        configureStroke(paintOuter, accent, 0.9, r * 0.014f)
        configureStroke(paintMiddle, accent, 0.7, r * 0.016f)
        configureStroke(paintBracket, accent, 0.45, r * 0.012f)
        configureStroke(paintSpoke, accent, 0.15 + 0.55 * s.glow, r * 0.008f)
        configureStroke(paintFacet, accent, 0.5, r * 0.007f)
        paintMembraneEdge.color = withAlpha(accent, 0.6)
        paintMembraneEdge.strokeWidth = r * 0.008f
        paintParticle.color = withAlpha(accent, 1.0)

        // 2) outer segmented ring (slow direct rotation, deformable)
        drawRing(
            canvas, cx, cy, r * OUTER_RING_FRACTION * s.shellExpansion.toFloat(),
            OUTER_SEGMENTS, OUTER_SPAN_DEG, OUTER_GAP_DEG, s.shellRotation.toFloat(),
            s.wobbleAmp * 0.6, s.wobblePhase.toFloat(), s.wobbleHarmonics,
            pointsPerArc(quality), paintOuter,
        )

        // 3) middle arcs (counter-rotation, full wobble — the deformable shell);
        //    spectrumIntensity (no band data on the wire) shimmers here.
        paintMiddle.alpha = (255 * (0.7 + 0.3 * s.spectrumShimmer)).toInt().coerceIn(0, 255)
        drawRing(
            canvas, cx, cy, r * MIDDLE_RING_FRACTION * s.shellExpansion.toFloat(),
            MIDDLE_SEGMENTS, MIDDLE_SPAN_DEG, MIDDLE_GAP_DEG, s.shell2Rotation.toFloat(),
            s.wobbleAmp, (s.wobblePhase * 1.7 + 1.3).toFloat(), s.wobbleHarmonics,
            pointsPerArc(quality), paintMiddle,
        )

        // 4) inner brackets (crisp counterpoint, no wobble)
        drawRing(
            canvas, cx, cy, r * BRACKET_RING_FRACTION, BRACKET_SEGMENTS,
            BRACKET_SPAN_DEG, BRACKET_GAP_DEG, s.bracketRotation.toFloat(),
            0.0, 0f, 1, maxOf(4, pointsPerArc(quality) / 2), paintBracket,
        )

        // 5) energy spokes between brackets and middle ring
        val spokes = spokeCount(quality)
        var i = 0
        while (i < spokes) {
            val a = (s.bracketRotation * 0.5).toFloat() + (2f * PI.toFloat() * i / spokes)
            canvas.drawLine(
                cx + r * SPOKE_INNER * kotlin.math.cos(a),
                cy + r * SPOKE_INNER * sin(a),
                cx + r * SPOKE_OUTER * kotlin.math.cos(a),
                cy + r * SPOKE_OUTER * sin(a),
                paintSpoke,
            )
            i++
        }

        // 6) eye backing disc
        canvas.drawCircle(cx, cy, eyeR, paintEyeFill)
        paintScleraEdge.strokeWidth = r * 0.007f
        canvas.drawCircle(cx, cy, eyeR * 0.86f, paintScleraEdge)

        // 7) angular iris — the facet signature (octagon, slowly counter-tilting)
        val irisR = eyeR * IRIS_FRACTION
        paintIris.shader = irisShader(tone, irisR, cx, cy)
        path.reset()
        fillPolygon(path, cx, cy, irisR, IRIS_FACETS, s.irisRotation.toFloat())
        canvas.drawPath(path, paintIris)
        paintIris.shader = null
        val pupilR = eyeR * PUPIL_FRACTION * s.pupilScale.toFloat()
        i = 0
        while (i < IRIS_FACETS) {
            val a = s.irisRotation.toFloat() + (2f * PI.toFloat() * i / IRIS_FACETS)
            canvas.drawLine(
                cx + pupilR * 0.9f * kotlin.math.cos(a),
                cy + pupilR * 0.9f * sin(a),
                cx + irisR * 0.94f * kotlin.math.cos(a),
                cy + irisR * 0.94f * sin(a),
                paintFacet,
            )
            i++
        }

        // 8) pupil (dilated pupil travels less — keeps it inside the iris)
        val travel = eyeR * PUPIL_FRACTION / maxOf(1f, s.pupilScale.toFloat())
        val px = cx + (s.pupilOffsetX * travel).toFloat()
        val py = cy + (s.pupilOffsetY * travel).toFloat()
        canvas.drawCircle(px, py, pupilR, paintPupil)

        // 9) reflection glint
        canvas.drawCircle(
            px - pupilR * 0.34f, py - pupilR * 0.36f, pupilR * 0.22f, paintGlint,
        )

        // 10) mechanical shutter eyelids
        if (s.eyeOpen < 0.995) {
            drawEyelids(canvas, cx, cy, eyeR, s.eyeOpen.toFloat())
        }

        // 11) energy particles (ambient motes between shell layers)
        drawParticles(canvas, cx, cy, r, time, s.particleEnergy.toFloat(), quality, accent)
    }

    private fun configureStroke(paint: Paint, color: Int, alpha: Double, width: Float) {
        paint.color = withAlpha(color, alpha)
        paint.strokeWidth = width
    }

    private fun drawRing(
        canvas: Canvas,
        cx: Float,
        cy: Float,
        radius: Float,
        segments: Int,
        spanDeg: Float,
        gapDeg: Float,
        rotation: Float,
        wobbleAmp: Double,
        wobblePhase: Float,
        harmonics: Int,
        pointsPerArc: Int,
        paint: Paint,
    ) {
        val step = (spanDeg + gapDeg) * DEG
        val span = spanDeg * DEG
        var seg = 0
        while (seg < segments) {
            val a0 = rotation + seg * step
            val n = fillArcPoints(
                arcBuffer, cx, cy, radius, a0, span,
                pointsPerArc.coerceIn(2, MAX_ARC_POINTS),
                wobbleAmp, wobblePhase, harmonics,
            )
            path.reset()
            path.moveTo(arcBuffer[0], arcBuffer[1])
            var i = 1
            while (i < n) {
                path.lineTo(arcBuffer[i * 2], arcBuffer[i * 2 + 1])
                i++
            }
            canvas.drawPath(path, paint)
            seg++
        }
    }

    /** r(θ) = radius · (1 + wobbleAmp · sin(harmonics·θ + phase)) — the
     * deformable shell; fills [buffer] with x,y pairs, returns point count. */
    private fun fillArcPoints(
        buffer: FloatArray,
        cx: Float,
        cy: Float,
        radius: Float,
        startAngle: Float,
        sweep: Float,
        pointCount: Int,
        wobbleAmp: Double,
        wobblePhase: Float,
        harmonics: Int,
    ): Int {
        val n = pointCount.coerceIn(2, buffer.size / 2)
        val h = harmonics.coerceIn(1, 8)
        val wob = if (wobbleAmp.isFinite()) wobbleAmp.coerceIn(0.0, 0.2) else 0.0
        var i = 0
        while (i < n) {
            val t = i.toFloat() / (n - 1)
            val a = startAngle + sweep * t
            val ri = radius * (1.0 + wob * sin(h * a + wobblePhase)).toFloat()
            buffer[i * 2] = cx + ri * kotlin.math.cos(a)
            buffer[i * 2 + 1] = cy + ri * sin(a)
            i++
        }
        return n
    }

    private fun fillPolygon(path: Path, cx: Float, cy: Float, radius: Float, facets: Int, rotation: Float) {
        var i = 0
        while (i < facets) {
            val a = rotation + (2f * PI.toFloat() * i / facets)
            val x = cx + radius * kotlin.math.cos(a)
            val y = cy + radius * sin(a)
            if (i == 0) path.moveTo(x, y) else path.lineTo(x, y)
            i++
        }
        path.close()
    }

    private fun drawEyelids(canvas: Canvas, cx: Float, cy: Float, eyeR: Float, open: Float) {
        val closePerSide = (1f - open) * eyeR
        val topY = cy - eyeR + closePerSide
        val bottomY = cy + eyeR - closePerSide
        eyeClipPath.reset()
        eyeClipPath.addCircle(cx, cy, eyeR + 1f, Path.Direction.CW)
        canvas.save()
        canvas.clipPath(eyeClipPath)
        lidRect.set(cx - eyeR - 4f, cy - eyeR - 6f, cx + eyeR + 4f, topY)
        canvas.drawRect(lidRect, paintMembrane)
        canvas.drawLine(cx - eyeR - 4f, topY, cx + eyeR + 4f, topY, paintMembraneEdge)
        lidRect.set(cx - eyeR - 4f, bottomY, cx + eyeR + 4f, cy + eyeR + 6f)
        canvas.drawRect(lidRect, paintMembrane)
        canvas.drawLine(cx - eyeR - 4f, bottomY, cx + eyeR + 4f, bottomY, paintMembraneEdge)
        canvas.restore()
    }

    private fun drawParticles(
        canvas: Canvas,
        cx: Float,
        cy: Float,
        r: Float,
        time: Float,
        energy: Float,
        quality: CephalonRenderQuality,
        accent: Int,
    ) {
        val maxCount = particleMax(quality)
        if (maxCount <= 0) return
        val count = min(
            ceil(maxCount * energy.coerceIn(0f, 1f)).toInt(),
            particles.size,
        )
        val e = energy.coerceIn(0f, 1f)
        var i = 0
        while (i < count) {
            val q = particles[i]
            val speed = q.speed * (0.6f + 0.8f * e)
            val a = q.angle + speed * time
            val radius = r * q.radiusFrac * (1f + 0.02f * sin(time * q.speed * 3f + q.phase))
            val alpha = ((0.25 + 0.75 * e) * (0.55 + 0.45 * sin(time * 2.2 + q.phase)))
                .coerceIn(0.0, 1.0)
            paintParticle.color = withAlpha(accent, alpha)
            canvas.drawCircle(
                cx + radius * kotlin.math.cos(a),
                cy + radius * sin(a),
                r * q.sizeFrac,
                paintParticle,
            )
            i++
        }
    }

    // ── quality → density (adaptive degradation targets) ────────────────────

    private fun pointsPerArc(quality: CephalonRenderQuality): Int = when (quality) {
        CephalonRenderQuality.High -> 16
        CephalonRenderQuality.Medium -> 12
        CephalonRenderQuality.Low -> 8
        CephalonRenderQuality.Minimal -> 5
    }

    private fun particleMax(quality: CephalonRenderQuality): Int = when (quality) {
        CephalonRenderQuality.High -> MAX_PARTICLES
        CephalonRenderQuality.Medium -> 12
        CephalonRenderQuality.Low -> 6
        CephalonRenderQuality.Minimal -> 0
    }

    private fun spokeCount(quality: CephalonRenderQuality): Int = when (quality) {
        CephalonRenderQuality.High -> MAX_SPOKES
        CephalonRenderQuality.Medium -> 4
        CephalonRenderQuality.Low -> 3
        CephalonRenderQuality.Minimal -> 0
    }

    // ── shader caches (rebuilt only on tone/radius-bucket change) ───────────

    private fun glowShader(tone: CephalonTone, glowR: Float): Shader {
        val bucket = (glowR / 8f).toInt()
        val key = (bucket.toLong() shl 8) or tone.ordinal.toLong()
        return glowShaders.getOrPut(key) {
            if (glowShaders.size > 48) glowShaders.clear()
            RadialGradient(
                0f, 0f, maxOf(glowR, 1f),
                withAlpha(CephalonPalette.resolve(tone), 0.55),
                Color.TRANSPARENT,
                Shader.TileMode.CLAMP,
            )
        }
    }

    private fun irisShader(tone: CephalonTone, irisR: Float, cx: Float, cy: Float): Shader {
        val bucket = (irisR / 8f).toInt()
        val key = (bucket shl 8) or tone.ordinal
        return irisShaders.getOrPut(key) {
            if (irisShaders.size > 48) irisShaders.clear()
            val accent = CephalonPalette.resolve(tone)
            RadialGradient(
                cx, cy, maxOf(irisR, 1f),
                blend(accent, Color.WHITE, 0.45),
                blend(accent, CephalonPalette.IrisDeep, 0.55),
                Shader.TileMode.CLAMP,
            )
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private fun withAlpha(color: Int, alpha: Double): Int = Color.argb(
        (255 * alpha.coerceIn(0.0, 1.0)).toInt(),
        Color.red(color), Color.green(color), Color.blue(color),
    )

    private fun blend(c1: Int, c2: Int, t: Double): Int {
        val f = t.coerceIn(0.0, 1.0)
        return Color.argb(
            255,
            lerp(Color.red(c1), Color.red(c2), f),
            lerp(Color.green(c1), Color.green(c2), f),
            lerp(Color.blue(c1), Color.blue(c2), f),
        )
    }

    private fun lerp(a: Int, b: Int, t: Double): Int = (a + (b - a) * t).toInt()

    private fun buildParticles(count: Int, seed: Long): Array<Particle> {
        val rand = Random(seed)
        return Array(count) {
            Particle(
                angle = rand.nextFloat() * 2f * PI.toFloat(),
                radiusFrac = (0.8f + 0.45f * rand.nextFloat()),
                speed = (0.15f + 0.35f * rand.nextFloat()) * (if (rand.nextBoolean()) 1f else -1f),
                phase = rand.nextFloat() * 2f * PI.toFloat(),
                sizeFrac = 0.006f + 0.010f * rand.nextFloat(),
            )
        }
    }
}
