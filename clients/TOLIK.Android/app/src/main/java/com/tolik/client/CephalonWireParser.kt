package com.tolik.client

import com.google.gson.JsonParser
import com.google.gson.JsonObject

// Pure-JVM parser for the cephalon_expression WS frame (Gson — testable without
// an Android framework; org.json is stubbed in local unit tests).
//
// Wire shape (verified field-by-field in Core tests, commit 4def5df):
// {"type":"event","payload":{
//     "kind":"cephalon_expression",
//     "expression":{"eyeScale":1.0,"eyeOpenness":0.75,…,"mode":"Idle"},
//     "at":"2026-10-10T12:00:00Z"}}
//
// Untrusted data rules (SECURITY §22/§23): the frame is DATA, never authority.
// Any malformation → null (the event is ignored safely, never crash, never a
// guessed state). The `at` timestamp is metadata — the renderer consumes only
// the normalized fields; honesty about staleness is the caller's status line.

object CephalonWireParser {

    /** Returns the normalized [ExpressionOutput] when [text] is a well-formed
     * cephalon_expression event; null for everything else (malformed JSON, other
     * event kinds, missing/garbage expression object). Never throws. */
    fun parseFrame(text: String): ExpressionOutput? {
        val root = try {
            JsonParser.parseString(text)
        } catch (_: Exception) {
            return null
        }
        if (!root.isJsonObject) return null
        val obj = root.asJsonObject

        if (asString(obj, "type") != "event") return null
        val payload = asObject(obj, "payload") ?: return null
        val kind = asString(payload, "kind") ?: return null
        if (kind != "cephalon_expression") return null
        val expr = asObject(payload, "expression") ?: return null

        val n = ExpressionOutput.NEUTRAL
        return ExpressionOutput(
            eyeScale = asDouble(expr, "eyeScale", n.eyeScale),
            eyeOpenness = asDouble(expr, "eyeOpenness", n.eyeOpenness),
            eyeRotation = asDouble(expr, "eyeRotation", n.eyeRotation),
            asymmetry = asDouble(expr, "asymmetry", n.asymmetry),
            shellExpansion = asDouble(expr, "shellExpansion", n.shellExpansion),
            shellDeformation = asDouble(expr, "shellDeformation", n.shellDeformation),
            rotation = asDouble(expr, "rotation", n.rotation),
            pulseAmplitude = asDouble(expr, "pulseAmplitude", n.pulseAmplitude),
            glowIntensity = asDouble(expr, "glowIntensity", n.glowIntensity),
            particleDensity = asDouble(expr, "particleDensity", n.particleDensity),
            spectrumIntensity = asDouble(expr, "spectrumIntensity", n.spectrumIntensity),
            movementSpeed = asDouble(expr, "movementSpeed", n.movementSpeed),
            attentionAngle = asDouble(expr, "attentionAngle", n.attentionAngle),
            attentionIntensity = asDouble(expr, "attentionIntensity", n.attentionIntensity),
            transitionEnergy = asDouble(expr, "transitionEnergy", n.transitionEnergy),
            mode = ExpressionMode.fromWire(asString(expr, "mode")),
        ).normalized()
    }

    private fun asString(obj: JsonObject, name: String): String? = try {
        val v = obj.get(name) ?: return null
        if (v.isJsonPrimitive && v.asJsonPrimitive.isString) v.asString else null
    } catch (_: Exception) {
        null
    }

    private fun asObject(obj: JsonObject, name: String): JsonObject? = try {
        val v = obj.get(name) ?: return null
        if (v.isJsonObject) v.asJsonObject else null
    } catch (_: Exception) {
        null
    }

    /** Lenient number read: missing / null / non-number / unparseable → fallback
     * (the neutral default). Normalization clamps afterwards. */
    private fun asDouble(obj: JsonObject, name: String, fallback: Double): Double = try {
        val v = obj.get(name) ?: return fallback
        if (v.isJsonNull || !v.isJsonPrimitive || !v.asJsonPrimitive.isNumber) {
            fallback
        } else {
            val d = v.asDouble
            if (d.isFinite()) d else fallback
        }
    } catch (_: Exception) {
        fallback
    }
}
