package com.tolik.client

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

// Wire-frame parsing for cephalon_expression (AN-P2): the exact shape Core
// broadcasts (commit 4def5df), safe handling of unknown kinds and malformed
// frames — untrusted data never crashes, never fabricates state.
class CephalonWireParserTest {

    /** The verified wire shape (field-for-field, from Core tests / WS smoke). */
    private val exactFrame = """
        {"type":"event","payload":{"kind":"cephalon_expression","expression":{
        "eyeScale":1.0,"eyeOpenness":0.75,"eyeRotation":0,"asymmetry":0,
        "shellExpansion":0,"shellDeformation":0,"rotation":0,"pulseAmplitude":0.08,
        "glowIntensity":0.25,"particleDensity":0.1,"spectrumIntensity":0,
        "movementSpeed":0.15,"attentionAngle":0,"attentionIntensity":0,
        "transitionEnergy":0.3,"mode":"Idle"},"at":"2026-10-10T12:00:00Z"}}
    """.trimIndent().replace("\n", "")

    @Test
    fun `exact wire shape parses to neutral idle`() {
        val out = CephalonWireParser.parseFrame(exactFrame)
        assertEquals(ExpressionOutput.NEUTRAL, out)
    }

    @Test
    fun `non-neutral fields survive the round trip`() {
        val frame = """
            {"type":"event","payload":{"kind":"cephalon_expression","expression":{
            "eyeScale":1.2,"eyeOpenness":0.4,"glowIntensity":0.8,
            "movementSpeed":0.9,"attentionAngle":1.2,"attentionIntensity":0.5,
            "mode":"Thinking"},"at":"2026-10-10T12:00:01Z"}}
        """.trimIndent().replace("\n", "")
        val out = requireNotNull(CephalonWireParser.parseFrame(frame))
        assertEquals(1.2, out.eyeScale, 1e-9)
        assertEquals(0.4, out.eyeOpenness, 1e-9)
        assertEquals(0.8, out.glowIntensity, 1e-9)
        assertEquals(0.9, out.movementSpeed, 1e-9)
        assertEquals(1.2, out.attentionAngle, 1e-9)
        assertEquals(0.5, out.attentionIntensity, 1e-9)
        assertEquals(ExpressionMode.Thinking, out.mode)
        // Absent fields fall back to neutral defaults.
        assertEquals(ExpressionOutput.NEUTRAL.rotation, out.rotation, 1e-9)
        assertEquals(ExpressionOutput.NEUTRAL.shellExpansion, out.shellExpansion, 1e-9)
    }

    @Test
    fun `unknown event kind is ignored`() {
        val frame = """{"type":"event","payload":{"kind":"TaskStarted","taskId":"t1"}}"""
        assertNull(CephalonWireParser.parseFrame(frame))
    }

    @Test
    fun `missing kind is ignored`() {
        val frame = """{"type":"event","payload":{"expression":{"mode":"Idle"}}}"""
        assertNull(CephalonWireParser.parseFrame(frame))
    }

    @Test
    fun `missing expression object is ignored`() {
        val frame = """{"type":"event","payload":{"kind":"cephalon_expression","at":"2026"}}"""
        assertNull(CephalonWireParser.parseFrame(frame))
    }

    @Test
    fun `malformed json is ignored`() {
        assertNull(CephalonWireParser.parseFrame("{not json"))
        assertNull(CephalonWireParser.parseFrame(""))
        assertNull(CephalonWireParser.parseFrame("42"))
        assertNull(CephalonWireParser.parseFrame("[1,2,3]"))
        assertNull(CephalonWireParser.parseFrame("null"))
    }

    @Test
    fun `wrong envelope type is ignored`() {
        val frame = """{"type":"hello","payload":{"kind":"cephalon_expression"}}"""
        assertNull(CephalonWireParser.parseFrame(frame))
    }

    @Test
    fun `expression as wrong type is ignored`() {
        val frame = """{"type":"event","payload":{"kind":"cephalon_expression",
            "expression":"Idle"}}""".replace("\n", "")
        assertNull(CephalonWireParser.parseFrame(frame))
    }

    @Test
    fun `non-number and overflowing fields fall back to neutral`() {
        // 1e999 parses to +Infinity → non-finite → neutral default.
        val frame = """
            {"type":"event","payload":{"kind":"cephalon_expression","expression":{
            "eyeScale":1e999,"eyeOpenness":"wide","mode":"Idle"}}}
        """.trimIndent().replace("\n", "")
        val out = requireNotNull(CephalonWireParser.parseFrame(frame))
        assertEquals(ExpressionOutput.NEUTRAL.eyeScale, out.eyeScale, 1e-9)
        assertEquals(ExpressionOutput.NEUTRAL.eyeOpenness, out.eyeOpenness, 1e-9)
        // Well-formed sibling fields still parse.
        assertEquals(ExpressionMode.Idle, out.mode)
    }

    @Test
    fun `task event frames still route to the task path`() {
        // The transport's org.json path handles these; the parser must NOT
        // swallow them (returns null → fallthrough).
        val frame = """{"type":"event","payload":{"kind":"TaskProgress",
            "taskId":"abc","detail":"x"}}""".replace("\n", "")
        assertNull(CephalonWireParser.parseFrame(frame))
    }
}
