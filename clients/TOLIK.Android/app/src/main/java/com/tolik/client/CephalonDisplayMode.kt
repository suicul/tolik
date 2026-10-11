package com.tolik.client

// Display modes (docs/android-app.md §3, §5 of the master plan): Companion /
// Fullscreen Presence / Conversation / Voice / Compact — a typed enum + layout
// presets. Adding a mode means adding a table row, not scattering conditionals
// through the Activity (desktop ModePolicyTable semantics).
//
// The Cephalon palette is preserved across all modes; the surrounding app
// chrome follows the Material theme (light/dark system setting respected).

/** The five presentation modes of the TOLIK mobile embodiment. */
enum class CephalonDisplayMode {
    /** Default: the Cephalon + status + chat entry + voice entry. */
    Companion,

    /** The Cephalon nearly full-screen, minimal UI. */
    FullscreenPresence,

    /** The Cephalon + the chat primary (history + input). */
    Conversation,

    /** The Cephalon large + mic + listening/speaking indicator + barge-in. */
    Voice,

    /** A small state widget strip. */
    Compact,
}

/** Which functional screen fills the area below the Cephalon. */
enum class CephalonContentScreen { Chat, Voice, Tasks, None }

/**
 * Layout preset for one display mode. [cephalonWeight] is the LinearLayout
 * weight of the Cephalon container (the content frame gets the remainder).
 */
data class CephalonModeLayout(
    val cephalonWeight: Float,
    val showAppHeader: Boolean,
    val showBottomNav: Boolean,
    val content: CephalonContentScreen,
    val showChatHistory: Boolean,
    val showVoiceEntry: Boolean,
    val showModeSwitcher: Boolean,
)

object CephalonModeLayouts {

    private val table: Map<CephalonDisplayMode, CephalonModeLayout> = mapOf(
        //                     weight header  nav    content history voice switch
        CephalonDisplayMode.Companion to CephalonModeLayout(
            cephalonWeight = 0.42f, showAppHeader = true, showBottomNav = true,
            content = CephalonContentScreen.Chat, showChatHistory = false,
            showVoiceEntry = true, showModeSwitcher = true,
        ),
        CephalonDisplayMode.FullscreenPresence to CephalonModeLayout(
            cephalonWeight = 1.0f, showAppHeader = false, showBottomNav = false,
            content = CephalonContentScreen.None, showChatHistory = false,
            showVoiceEntry = false, showModeSwitcher = false,
        ),
        CephalonDisplayMode.Conversation to CephalonModeLayout(
            cephalonWeight = 0.26f, showAppHeader = true, showBottomNav = true,
            content = CephalonContentScreen.Chat, showChatHistory = true,
            showVoiceEntry = false, showModeSwitcher = true,
        ),
        CephalonDisplayMode.Voice to CephalonModeLayout(
            cephalonWeight = 0.55f, showAppHeader = true, showBottomNav = true,
            content = CephalonContentScreen.Voice, showChatHistory = false,
            showVoiceEntry = false, showModeSwitcher = true,
        ),
        CephalonDisplayMode.Compact to CephalonModeLayout(
            cephalonWeight = 0.16f, showAppHeader = true, showBottomNav = true,
            content = CephalonContentScreen.None, showChatHistory = false,
            showVoiceEntry = false, showModeSwitcher = true,
        ),
    )

    /** Fail-closed lookup: an unmapped mode falls back to the calm Companion
     * preset rather than inventing behavior. */
    fun forMode(mode: CephalonDisplayMode): CephalonModeLayout =
        table[mode] ?: table.getValue(CephalonDisplayMode.Companion)

    val allModes: Collection<CephalonDisplayMode> get() = table.keys
}
