package com.tolik.client

// Adaptive render gate for the mobile Cephalon — a simplified port of
// src/TOLIK.Desktop/Performance/FrameGovernor.cs (docs/android-app.md §21):
// caps FPS to the current target (30 idle / 60 active), measures frame time
// (EWMA) and degrades effect quality under sustained slow frames. Pure logic —
// no Android types, deterministic under tests.

/** Adaptive quality level; the renderer maps it to particle counts / arc resolution. */
enum class CephalonRenderQuality {
    Minimal, Low, Medium, High,
}

class CephalonFrameGovernor(targetFps: Double) {

    companion object {
        const val EWMA_ALPHA = 0.12
        const val DEGRADE_FACTOR = 1.30   // degrade when ewma > budget × 1.30
        const val RECOVER_FACTOR = 0.55   // recovery candidate when ewma ≤ budget × 0.55
        const val MIN_CHANGE_COOLDOWN_SEC = 1.5
        const val RECOVER_HOLD_SEC = 2.0
        const val MIN_FPS = 1.0
        const val MAX_FPS = 60.0          // mobile ceiling — 60 active (docs §21)
        const val DEFAULT_IDLE_FPS = 30.0
        const val DEFAULT_ACTIVE_FPS = 60.0
    }

    private var targetFps = sanitize(targetFps)
    private var lastRenderAt = 0.0
    private var hasLastRender = false
    private var ewmaMs = 0.0
    private var hasEwma = false
    private var quality = CephalonRenderQuality.High
    private var lastQualityChangeAt = Double.NEGATIVE_INFINITY
    private var goodSince: Double? = null

    /** Current FPS ceiling (always within [MIN_FPS, MAX_FPS]). */
    val fps: Double get() = targetFps

    /** CPU render budget in ms: half the frame interval — TOLIK must stay light. */
    val budgetMs: Double get() = 500.0 / targetFps

    val qualityLevel: CephalonRenderQuality get() = quality

    /** EWMA of measured frame time in ms (0 until the first frame). */
    val ewmaFrameMs: Double get() = ewmaMs

    /** Changes the FPS ceiling (clamped) — used on idle/active switches. */
    fun setTargetFps(fps: Double) {
        targetFps = sanitize(fps)
    }

    /** Cheap gate called every loop tick; allocates nothing. */
    fun shouldRender(nowSeconds: Double): Boolean {
        val minInterval = 1.0 / targetFps
        // 0.92 tolerance absorbs vsync jitter so we don't systematically drop
        // to (n-1) fps.
        if (!hasLastRender || nowSeconds - lastRenderAt >= minInterval * 0.92) {
            lastRenderAt = nowSeconds
            hasLastRender = true
            return true
        }
        return false
    }

    /** Feeds back the measured time (ms) of one rendered frame; adapts quality. */
    fun onFrameRendered(frameMs: Double, nowSeconds: Double) {
        val sample = if (frameMs.isFinite()) frameMs.coerceIn(0.0, 10_000.0) else 0.0
        ewmaMs = if (hasEwma) ewmaMs + EWMA_ALPHA * (sample - ewmaMs) else sample
        hasEwma = true

        if (nowSeconds - lastQualityChangeAt < MIN_CHANGE_COOLDOWN_SEC) return

        val budget = budgetMs
        when {
            // Degrade fast, recover slow (hysteresis prevents flapping).
            quality > CephalonRenderQuality.Minimal && ewmaMs > budget * DEGRADE_FACTOR -> {
                quality = CephalonRenderQuality.entries[quality.ordinal - 1]
                lastQualityChangeAt = nowSeconds
                goodSince = null
            }
            quality < CephalonRenderQuality.High && ewmaMs <= budget * RECOVER_FACTOR -> {
                val since = goodSince ?: nowSeconds.also { goodSince = it }
                if (nowSeconds - since >= RECOVER_HOLD_SEC) {
                    quality = CephalonRenderQuality.entries[quality.ordinal + 1]
                    lastQualityChangeAt = nowSeconds
                    goodSince = null
                }
            }
            else -> goodSince = null
        }
    }

    private fun sanitize(fps: Double): Double =
        if (fps.isFinite()) fps.coerceIn(MIN_FPS, MAX_FPS) else MIN_FPS
}
