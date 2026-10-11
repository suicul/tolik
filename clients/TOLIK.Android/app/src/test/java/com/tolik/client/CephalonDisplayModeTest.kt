package com.tolik.client

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

// Display modes (docs/android-app.md §5): typed enum + layout presets —
// all five modes present, sane presets, fail-closed lookup.
class CephalonDisplayModeTest {

    @Test
    fun `all five display modes exist`() {
        assertEquals(
            setOf(
                CephalonDisplayMode.Companion,
                CephalonDisplayMode.FullscreenPresence,
                CephalonDisplayMode.Conversation,
                CephalonDisplayMode.Voice,
                CephalonDisplayMode.Compact,
            ),
            CephalonModeLayouts.allModes.toSet(),
        )
    }

    @Test
    fun `every mode resolves to a preset`() {
        for (mode in CephalonDisplayMode.entries) {
            val layout = CephalonModeLayouts.forMode(mode)
            assertTrue("weight of $mode must be in (0, 1]", layout.cephalonWeight in 0f..1f)
            assertTrue(layout.cephalonWeight > 0f)
        }
    }

    @Test
    fun `companion is the default preset`() {
        val layout = CephalonModeLayouts.forMode(CephalonDisplayMode.Companion)
        assertTrue(layout.showAppHeader)
        assertTrue(layout.showBottomNav)
        assertTrue(layout.showModeSwitcher)
        assertTrue(layout.showVoiceEntry)
        // Companion shows the chat ENTRY, not the full history.
        assertEquals(CephalonContentScreen.Chat, layout.content)
        assertEquals(false, layout.showChatHistory)
    }

    @Test
    fun `fullscreen presence is minimal chrome`() {
        val layout = CephalonModeLayouts.forMode(CephalonDisplayMode.FullscreenPresence)
        assertEquals(1.0f, layout.cephalonWeight, 1e-6f)
        assertEquals(false, layout.showAppHeader)
        assertEquals(false, layout.showBottomNav)
        assertEquals(false, layout.showModeSwitcher)
        assertEquals(CephalonContentScreen.None, layout.content)
    }

    @Test
    fun `conversation shows the full chat`() {
        val layout = CephalonModeLayouts.forMode(CephalonDisplayMode.Conversation)
        assertEquals(CephalonContentScreen.Chat, layout.content)
        assertTrue(layout.showChatHistory)
        assertEquals(false, layout.showVoiceEntry)
        assertTrue(layout.cephalonWeight < 0.5f)
    }

    @Test
    fun `voice mode is large with the voice screen`() {
        val layout = CephalonModeLayouts.forMode(CephalonDisplayMode.Voice)
        assertEquals(CephalonContentScreen.Voice, layout.content)
        assertTrue("voice cephalon should dominate", layout.cephalonWeight >= 0.5f)
    }

    @Test
    fun `compact is a small strip`() {
        val layout = CephalonModeLayouts.forMode(CephalonDisplayMode.Compact)
        assertTrue(layout.cephalonWeight <= 0.2f)
        assertEquals(CephalonContentScreen.None, layout.content)
        assertTrue(layout.showBottomNav) // navigation stays reachable
    }
}
