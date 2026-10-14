package com.tolik.client

import android.Manifest
import android.annotation.SuppressLint
import android.content.pm.PackageManager
import android.os.Bundle
import android.os.SystemClock
import android.view.View
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ProgressBar
import android.widget.TextView
import android.widget.Toast
import androidx.appcompat.app.AlertDialog
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import androidx.lifecycle.lifecycleScope
import androidx.recyclerview.widget.LinearLayoutManager
import androidx.recyclerview.widget.RecyclerView
import com.google.android.material.bottomnavigation.BottomNavigationView
import kotlinx.coroutines.delay
import kotlinx.coroutines.channels.BufferOverflow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.launch
import java.util.Locale

/**
 * TOLIK Android thin client — the single Activity (AGENTS.md §36).
 *
 * This class wires the UI to the transport and NOTHING ELSE: no Core logic,
 * no authorization decisions, no model credentials. 401/403 responses are
 * surfaced as failures (settings prompt) — never local authorization, never
 * silent retry loops (SECURITY & TRUST MODEL §1, §14).
 */
class MainActivity : AppCompatActivity(), VoiceSessionController.View {

    private lateinit var sessionStore: SessionStore
    private lateinit var transport: OkHttpTolikTransport
    private lateinit var tracker: TaskTracker
    private lateinit var voice: VoiceSessionController

    private lateinit var chatAdapter: ChatAdapter
    private lateinit var taskAdapter: TaskAdapter

    // Views
    private lateinit var statusText: TextView
    private lateinit var screenChat: View
    private lateinit var screenVoice: View
    private lateinit var screenTasks: View
    private lateinit var chatList: RecyclerView
    private lateinit var chatInput: EditText
    private lateinit var chatProgress: ProgressBar
    private lateinit var voiceState: TextView
    private lateinit var voiceTranscript: TextView
    private lateinit var voiceReply: TextView
    private lateinit var voiceSpeakerHint: TextView
    private lateinit var voiceToggleButton: Button
    private lateinit var voiceCancelButton: Button
    private lateinit var taskList: RecyclerView
    private lateinit var tasksEmptyView: TextView

    // Cephalon presence (AN-P2) — the renderer holds no logic beyond drawing;
    // all state lives here in the thin Activity layer.
    private lateinit var cephContainer: View
    private lateinit var contentFrame: View
    private lateinit var cephView: CephalonView
    private lateinit var cephStatusText: TextView
    private lateinit var cephExitButton: Button
    private lateinit var compactHint: View
    private lateinit var chatVoiceEntry: Button

    /** Core expression events → the renderer (drop-oldest: the newest wins;
     * a stale 5 Hz burst never queues up). */
    private val expressionBus = MutableSharedFlow<ExpressionOutput>(
        extraBufferCapacity = 8,
        onBufferOverflow = BufferOverflow.DROP_OLDEST,
    )

    private enum class CephConnectionState { DISCONNECTED, CONNECTED, AUTH_FAILED }

    // Written from OkHttp WS threads, read from the main ticker.
    @Volatile
    private var cephState: CephConnectionState = CephConnectionState.DISCONNECTED
    private var lastExpressionAtMs: Long = 0L
    private var lastExpressionMode: ExpressionMode? = null
    private var displayMode: CephalonDisplayMode = CephalonDisplayMode.Companion
    private var currentScreenId: Int = 0

    private var eventsHandle: TolikEventsStreamHandle? = null

    private companion object {
        const val REQUEST_MIC = 41
        const val REQUEST_NOTIFICATIONS = 42

        /** A connected stream silent past this is dead → honest local idle,
         * never a held pose (desktop ExpressionLinkPresenter.StaleAfterSeconds
         * — Core heartbeats idle ~1 s and pushes changes ~5 Hz). */
        const val CEPHALON_STALE_AFTER_MS = 15_000L
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        TolikNotifier.ensureChannels(this)
        setContentView(R.layout.activity_main)

        sessionStore = SessionStore(this)
        transport = OkHttpTolikTransport(sessionStore.baseUrl)
        tracker = TaskTracker()
        voice = VoiceSessionController(
            transport = transport,
            player = TtsChunkPlayer(this),
            scope = lifecycleScope,
            sessionToken = { sessionStore.sessionToken },
        )
        voice.view = this

        bindViews()
        setupChat()
        setupTasks()
        setupVoice()
        setupHeader()
        setupCephalon()
        showScreen(R.id.screen_chat)
        applyDisplayMode(CephalonDisplayMode.Companion)

        // Expression events → renderer + honest status age ticker.
        lifecycleScope.launch {
            expressionBus.collect { expression ->
                lastExpressionAtMs = SystemClock.elapsedRealtime()
                lastExpressionMode = expression.mode
                cephView.setExpression(expression)
            }
        }
        lifecycleScope.launch {
            while (true) {
                delay(500)
                renderCephalonStatus()
            }
        }
    }

    override fun onStart() {
        super.onStart()
        refreshStatus()
        openEvents()
    }

    override fun onStop() {
        eventsHandle?.close()
        eventsHandle = null
        super.onStop()
    }

    override fun onDestroy() {
        voice.release()
        super.onDestroy()
    }

    // ─── Wiring ─────────────────────────────────────────────────────────────

    private fun bindViews() {
        statusText = findViewById(R.id.status_text)
        screenChat = findViewById(R.id.screen_chat)
        screenVoice = findViewById(R.id.screen_voice)
        screenTasks = findViewById(R.id.screen_tasks)
        chatList = findViewById(R.id.chat_list)
        chatInput = findViewById(R.id.chat_input)
        chatProgress = findViewById(R.id.chat_progress)
        voiceState = findViewById(R.id.voice_state)
        voiceTranscript = findViewById(R.id.voice_transcript)
        voiceReply = findViewById(R.id.voice_reply)
        voiceSpeakerHint = findViewById(R.id.voice_speaker_hint)
        voiceToggleButton = findViewById(R.id.voice_toggle_button)
        voiceCancelButton = findViewById(R.id.voice_cancel_button)
        taskList = findViewById(R.id.task_list)
        tasksEmptyView = findViewById(R.id.tasks_empty_view)
        cephContainer = findViewById(R.id.cephalon_container)
        contentFrame = findViewById(R.id.content_frame)
        cephView = findViewById(R.id.cephalon_view)
        cephStatusText = findViewById(R.id.cephalon_status)
        cephExitButton = findViewById(R.id.cephalon_exit)
        compactHint = findViewById(R.id.compact_hint)
        chatVoiceEntry = findViewById(R.id.chat_voice_entry)
    }

    private fun setupHeader() {
        findViewById<Button>(R.id.status_refresh_button).setOnClickListener { refreshStatus() }
        findViewById<Button>(R.id.settings_button).setOnClickListener { showSettingsDialog() }
        val nav = findViewById<BottomNavigationView>(R.id.bottom_nav)
        nav.setOnItemSelectedListener { item ->
            when (item.itemId) {
                R.id.nav_chat -> showScreen(R.id.screen_chat)
                R.id.nav_voice -> showScreen(R.id.screen_voice)
                R.id.nav_tasks -> showScreen(R.id.screen_tasks)
            }
            true
        }
    }

    private fun setupChat() {
        chatAdapter = ChatAdapter()
        chatList.layoutManager = LinearLayoutManager(this)
        chatList.adapter = chatAdapter
        val send = findViewById<Button>(R.id.chat_send)
        fun sendCurrent() {
            val text = chatInput.text?.toString()?.trim().orEmpty()
            if (text.isEmpty()) return
            chatInput.setText("")
            sendChat(text)
        }
        send.setOnClickListener { sendCurrent() }
        chatInput.setOnEditorActionListener { _, action, _ ->
            // imeOptions=actionSend on the input field.
            if (action == android.view.inputmethod.EditorInfo.IME_ACTION_SEND) {
                sendCurrent(); true
            } else false
        }
    }

    private fun setupTasks() {
        taskAdapter = TaskAdapter(
            onCancel = { task -> cancelTask(task) },
            onConfirm = { task -> showConfirmationDialog(task.id) },
        )
        taskList.layoutManager = LinearLayoutManager(this)
        taskList.adapter = taskAdapter
        tracker.addListener { runOnUiThread { renderTasks() } }
        findViewById<Button>(R.id.task_create_button).setOnClickListener {
            val description = findViewById<EditText>(R.id.task_input).text?.toString()?.trim().orEmpty()
            if (description.isEmpty()) return@setOnClickListener
            findViewById<EditText>(R.id.task_input).setText("")
            createTask(description)
        }
        maybeRequestNotifications()
    }

    private fun setupVoice() {
        voiceToggleButton.setOnClickListener {
            if (voice.currentState != VoiceSessionController.State.IDLE) {
                voice.bargeIn()
                return@setOnClickListener
            }
            if (ensureMicPermission()) voice.startTurn()
        }
        voiceCancelButton.setOnClickListener { voice.bargeIn() }
        // Companion-mode voice entry: same barge-in/permission semantics.
        chatVoiceEntry.setOnClickListener {
            if (voice.currentState != VoiceSessionController.State.IDLE) {
                voice.bargeIn()
                return@setOnClickListener
            }
            if (ensureMicPermission()) voice.startTurn()
        }
    }

    private fun showScreen(id: Int) {
        currentScreenId = id
        screenChat.visibility = if (id == R.id.screen_chat) View.VISIBLE else View.GONE
        screenVoice.visibility = if (id == R.id.screen_voice) View.VISIBLE else View.GONE
        screenTasks.visibility = if (id == R.id.screen_tasks) View.VISIBLE else View.GONE
    }

    // ─── Cephalon display modes (AN-P2: typed enum + layout presets) ────────

    private fun setupCephalon() {
        findViewById<Button>(R.id.mode_companion).setOnClickListener {
            applyDisplayMode(CephalonDisplayMode.Companion)
        }
        findViewById<Button>(R.id.mode_presence).setOnClickListener {
            applyDisplayMode(CephalonDisplayMode.FullscreenPresence)
        }
        findViewById<Button>(R.id.mode_conversation).setOnClickListener {
            applyDisplayMode(CephalonDisplayMode.Conversation)
        }
        findViewById<Button>(R.id.mode_voice).setOnClickListener {
            applyDisplayMode(CephalonDisplayMode.Voice)
        }
        findViewById<Button>(R.id.mode_compact).setOnClickListener {
            applyDisplayMode(CephalonDisplayMode.Compact)
        }
        cephExitButton.setOnClickListener { applyDisplayMode(CephalonDisplayMode.Companion) }
    }

    /** Applies the mode's layout preset (CephalonModeLayouts — data, not
     * scattered conditionals). The Cephalon palette is mode-independent. */
    private fun applyDisplayMode(mode: CephalonDisplayMode) {
        displayMode = mode
        val layout = CephalonModeLayouts.forMode(mode)

        findViewById<View>(R.id.status_header).visibility =
            if (layout.showAppHeader) View.VISIBLE else View.GONE
        findViewById<View>(R.id.mode_switcher).visibility =
            if (layout.showModeSwitcher) View.VISIBLE else View.GONE
        findViewById<View>(R.id.bottom_nav).visibility =
            if (layout.showBottomNav) View.VISIBLE else View.GONE
        cephExitButton.visibility =
            if (mode == CephalonDisplayMode.FullscreenPresence) View.VISIBLE else View.GONE

        val cephParams = cephContainer.layoutParams as LinearLayout.LayoutParams
        cephParams.weight = layout.cephalonWeight
        cephParams.height = 0
        cephContainer.layoutParams = cephParams

        val contentParams = contentFrame.layoutParams as LinearLayout.LayoutParams
        contentParams.weight = 1f - layout.cephalonWeight
        contentParams.height = 0
        contentFrame.layoutParams = contentParams
        contentFrame.visibility =
            if (mode == CephalonDisplayMode.FullscreenPresence) View.GONE else View.VISIBLE

        compactHint.visibility =
            if (mode == CephalonDisplayMode.Compact) View.VISIBLE else View.GONE

        when (layout.content) {
            CephalonContentScreen.None -> {
                screenChat.visibility = View.GONE
                screenVoice.visibility = View.GONE
                screenTasks.visibility = View.GONE
            }
            else -> showScreen(
                when (mode) {
                    // Conversation/Voice name their primary screen; Companion
                    // keeps whatever functional screen the user was on.
                    CephalonDisplayMode.Conversation -> R.id.screen_chat
                    CephalonDisplayMode.Voice -> R.id.screen_voice
                    else -> if (currentScreenId != 0) currentScreenId else R.id.screen_chat
                },
            )
        }
        // History/voice-entry chrome follows the mode policy.
        chatList.visibility =
            if (layout.content == CephalonContentScreen.Chat && layout.showChatHistory) {
                View.VISIBLE
            } else {
                View.GONE
            }
        chatVoiceEntry.visibility =
            if (layout.content == CephalonContentScreen.Chat && layout.showVoiceEntry) {
                View.VISIBLE
            } else {
                View.GONE
            }
    }

    // ─── Honest Cephalon status (директива §17/§20: реальные данные, никогда
    // не фейк: no connection → idle + offline; connected without events →
    // awaiting; 401/403 → auth, never fake-connected; stale > 15 s → local
    // idle + honest "stale", never a frozen pose; events → mode + age) ─────

    /** Called every ~500 ms — the desktop presenter's Tick(): drops a stale
     * drive and renders the honest status line. */
    private fun renderCephalonStatus() {
        val stale = dropStaleExpression()
        cephStatusText.text = when (cephState) {
            CephConnectionState.DISCONNECTED -> getString(R.string.cephalon_status_offline)
            CephConnectionState.AUTH_FAILED -> getString(R.string.cephalon_status_auth)
            CephConnectionState.CONNECTED -> when {
                lastExpressionAtMs == 0L -> getString(R.string.cephalon_status_awaiting)
                stale -> getString(
                    R.string.cephalon_status_stale,
                    lastExpressionMode?.wire ?: "Idle",
                    (SystemClock.elapsedRealtime() - lastExpressionAtMs) / 1000.0,
                )
                else -> String.format(
                    Locale.US,
                    "%s · %.1f с",
                    lastExpressionMode?.wire ?: "Idle",
                    (SystemClock.elapsedRealtime() - lastExpressionAtMs) / 1000.0,
                )
            }
        }
    }

    /** Staleness watchdog (desktop ExpressionLinkPresenter.Tick()): once the
     * last real event is older than [CEPHALON_STALE_AFTER_MS], the renderer
     * eases back to neutral local idle — the status says so honestly. */
    private fun dropStaleExpression(): Boolean {
        if (lastExpressionAtMs == 0L) return false
        val ageMs = SystemClock.elapsedRealtime() - lastExpressionAtMs
        if (ageMs <= CEPHALON_STALE_AFTER_MS) return false
        cephView.clearExpression()
        return true
    }

    // ─── Status header (thin: reflects Core, decides nothing) ───────────────

    private fun refreshStatus() {
        val token = sessionStore.sessionToken
        lifecycleScope.launch {
            var summary: String? = null
            // /health is unauthenticated on Core.
            val health = transport.getHealth()
            health.onSuccess { h ->
                summary = buildString {
                    append(if (h.core == true) "Core: OK" else "Core: недоступен")
                    if (h.locked == true) append(" · LOCKED")
                    append(" · voice=${h.voice == true} agent=${h.agent == true} memory=${h.memory == true}")
                }
            }
            // 401/403 → failure surfaces (no retry, no local authorization).
            val status = transport.getStatus(token)
            status.onSuccess { s ->
                summary = (summary ?: "") + "\n${s.status ?: "?"} · ${s.role ?: "?"}"
            }
            status.onFailure { failure ->
                summary = (summary ?: "") + "\n" + describeFailure(failure)
            }
            val agents = transport.listAgents(token)
            agents.onSuccess { list ->
                val connected = list.count { it.status == "Connected" }
                summary = (summary ?: "") + "\nАгенты: $connected подключено"
            }
            renderStatus(summary ?: getString(R.string.status_disconnected), health.isSuccess)
        }
    }

    private fun renderStatus(text: String, connected: Boolean) {
        statusText.text = text
        val color = when {
            !connected -> R.color.md_theme_error
            text.contains("LOCKED") -> R.color.md_theme_error
            else -> R.color.md_theme_on_surface_variant
        }
        statusText.setTextColor(getColor(color))
    }

    private fun describeFailure(failure: Throwable): String {
        val apiError = failure as? TolikApiException
        return when {
            apiError?.isAuthError == true -> getString(R.string.chat_auth_error)
            apiError != null -> getString(R.string.chat_error_prefix) + apiError.apiError
            else -> getString(R.string.chat_error_prefix) + (failure.message ?: "unknown")
        }
    }

    // ─── Settings (connection state + token minting at runtime) ─────────────

    /** Rebuilds the transport after a base URL change (config, §41). */
    private fun rebuildTransport() {
        transport = OkHttpTolikTransport(sessionStore.baseUrl)
    }

    private fun showSettingsDialog() {
        val container = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(48, 24, 48, 0)
        }
        val baseUrlInput = EditText(this).apply {
            hint = getString(R.string.settings_base_url_hint)
            setText(sessionStore.baseUrl)
        }
        val enrollInput = EditText(this).apply {
            hint = getString(R.string.settings_enroll_hint)
            inputType = android.text.InputType.TYPE_CLASS_TEXT or
                android.text.InputType.TYPE_TEXT_VARIATION_PASSWORD
        }
        container.addView(baseUrlInput)
        container.addView(Button(this).apply {
            text = getString(R.string.settings_dev_token_button)
            setOnClickListener {
                lifecycleScope.launch {
                    val result = transport.getLocalDevToken()
                    result.onSuccess { minted ->
                        // Token arrives from the device bootstrap/dev flow at
                        // runtime — never from code (§6).
                        sessionStore.sessionToken = minted.sessionToken
                        Toast.makeText(
                            this@MainActivity,
                            R.string.status_connected,
                            Toast.LENGTH_SHORT,
                        ).show()
                        refreshStatus()
                        openEvents()
                    }
                    result.onFailure { failure ->
                        val apiError = failure as? TolikApiException
                        val message = when (apiError?.statusCode) {
                            404 -> "dev-токен недоступен: нужен TOLIK_DEV_AUTO_TOKEN + loopback (до Genesis)"
                            else -> describeFailure(failure)
                        }
                        Toast.makeText(this@MainActivity, message, Toast.LENGTH_LONG).show()
                    }
                }
            }
        })
        container.addView(enrollInput)
        container.addView(Button(this).apply {
            text = getString(R.string.settings_enroll_button)
            setOnClickListener {
                val enrollmentToken = enrollInput.text?.toString()?.trim().orEmpty()
                if (enrollmentToken.isEmpty()) return@setOnClickListener
                lifecycleScope.launch {
                    val result = transport.enroll(
                        TolikEnrollRequest(
                            enrollmentToken = enrollmentToken,
                            // Real device identity: AndroidKeyStore keypair +
                            // persistent installation UUID (SECURITY §3/§11).
                            installationId = DeviceIdentity.installationId(this@MainActivity),
                            publicKey = DeviceIdentity.publicKeyBase64(this@MainActivity),
                        ),
                    )
                    result.onSuccess { enrolled ->
                        sessionStore.sessionToken = enrolled.sessionToken
                        Toast.makeText(
                            this@MainActivity,
                            R.string.status_connected,
                            Toast.LENGTH_SHORT,
                        ).show()
                        refreshStatus()
                        openEvents()
                    }
                    result.onFailure { failure ->
                        Toast.makeText(this@MainActivity, describeFailure(failure), Toast.LENGTH_LONG).show()
                    }
                }
            }
        })
        AlertDialog.Builder(this)
            .setTitle(R.string.settings_title)
            .setView(container)
            .setPositiveButton(R.string.settings_close) { _, _ ->
                val newUrl = baseUrlInput.text?.toString()?.trim().orEmpty()
                if (newUrl.isNotEmpty() && newUrl != sessionStore.baseUrl) {
                    sessionStore.baseUrl = newUrl
                    rebuildTransport()
                    refreshStatus()
                    openEvents()
                }
            }
            .show()
    }

    // ─── Chat (thin: /chat via transport, renders, loading state) ───────────

    private fun sendChat(message: String) {
        chatAdapter.addUser(message)
        chatAdapter.addLoading()
        chatProgress.visibility = View.VISIBLE
        chatList.scrollToPosition(chatAdapter.itemCount - 1)
        lifecycleScope.launch {
            val result = transport.chat(TolikChatRequest(message), sessionStore.sessionToken)
            chatProgress.visibility = View.GONE
            chatAdapter.removeLoading()
            result.onSuccess { response ->
                chatAdapter.addTolik(response.text ?: "…")
            }
            result.onFailure { failure ->
                chatAdapter.addError(describeFailure(failure))
            }
            chatList.scrollToPosition(chatAdapter.itemCount - 1)
        }
    }

    // ─── Tasks (AN-2) ───────────────────────────────────────────────────────

    private fun createTask(description: String) {
        lifecycleScope.launch {
            val result = transport.createTask(TolikTaskRequest(description), sessionStore.sessionToken)
            result.onSuccess { task ->
                tracker.upsertCreated(task)
            }
            result.onFailure { failure ->
                Toast.makeText(this@MainActivity, describeFailure(failure), Toast.LENGTH_LONG).show()
            }
        }
    }

    private fun cancelTask(task: TaskTracker.TrackedTask) {
        lifecycleScope.launch {
            val result = transport.cancelTask(task.id, sessionStore.sessionToken)
            result.onSuccess { updated ->
                tracker.applyFetched(updated)
            }
            result.onFailure { failure ->
                Toast.makeText(this@MainActivity, describeFailure(failure), Toast.LENGTH_LONG).show()
            }
        }
    }

    private fun renderTasks() {
        val snapshot = tracker.snapshot()
        tasksEmptyView.visibility = if (snapshot.isEmpty()) View.VISIBLE else View.GONE
        taskAdapter.submit(snapshot)
    }

    // ─── Realtime events (AN-2) ─────────────────────────────────────────────

    private fun openEvents() {
        val token = sessionStore.sessionToken ?: return
        eventsHandle?.close()
        eventsHandle = transport.openEvents(token, object : TolikEventsListener {
            override fun onConnected() {
                cephState = CephConnectionState.CONNECTED
                runOnUiThread {
                    refreshStatus()
                    renderCephalonStatus()
                }
            }

            override fun onDisconnected(clean: Boolean, cause: Throwable?) {
                // Auth failures surface in the header (no retry — the socket
                // stops reconnecting on 401); non-auth reconnects are inside
                // the transport's bounded backoff. 401 arrives as an explicit
                // TolikApiException from the transport — honest, never silent.
                val apiError = cause as? TolikApiException
                cephState = if (apiError?.isAuthError == true) {
                    CephConnectionState.AUTH_FAILED
                } else {
                    CephConnectionState.DISCONNECTED
                }
                // DropDrive (desktop ExpressionLinkPresenter): without a stream
                // the body falls back to local idle immediately — never a
                // held pose on dead strings. The previous connection's events
                // are history: after a reconnect the status honestly awaits
                // NEW events instead of aging the old ones.
                cephView.clearExpression()
                lastExpressionAtMs = 0L
                lastExpressionMode = null
                runOnUiThread {
                    if (apiError != null) {
                        renderStatus(describeFailure(apiError), connected = false)
                    }
                    renderCephalonStatus()
                }
            }

            override fun onCephalonExpression(expression: ExpressionOutput) {
                // WS payloads are untrusted data (§22/§23): they drive the
                // visual presence — they never grant anything, never change
                // permissions, never touch identity.
                expressionBus.tryEmit(expression)
            }

            override fun onTaskEvent(event: TolikWsTaskEvent) {
                // WS payloads are untrusted data (§22/§23): they refresh the
                // task view — they never grant anything.
                runOnUiThread {
                    tracker.applyEvent(event)
                    when (event.kind) {
                        "TaskCompleted" -> TolikNotifier.notifyTask(
                            this@MainActivity,
                            event.taskId.hashCode(),
                            getString(R.string.tab_tasks),
                            event.detail ?: getString(R.string.task_status_prefix) + "Completed",
                        )
                        "TaskFailed" -> TolikNotifier.notifyTask(
                            this@MainActivity,
                            event.taskId.hashCode(),
                            getString(R.string.tab_tasks),
                            event.detail ?: getString(R.string.task_status_prefix) + "Failed",
                        )
                        "TaskProgress" -> if (event.detail?.startsWith("awaiting_confirmation:") == true) {
                            promptConfirmationForTask(event.taskId)
                        }
                    }
                }
            }

            override fun onProactive(event: TolikWsProactive) {
                runOnUiThread {
                    TolikNotifier.notifyReminder(
                        this@MainActivity,
                        event.id?.hashCode() ?: 0,
                        event.text ?: "",
                    )
                }
            }

            // The mind's voice (MI-7): the already double-gated proactive message —
            // the owner sees it as a system notification (AN-2). The text is TOLIK's
            // own message, never caller data (§23).
            override fun onProactiveMind(event: TolikWsProactiveMind) {
                runOnUiThread {
                    TolikNotifier.notifyMind(
                        this@MainActivity,
                        event.at?.hashCode() ?: event.text?.hashCode() ?: 0,
                        event.text ?: "",
                    )
                }
            }

            // The return digest (CA-5): what happened while the owner was away.
            override fun onPresenceReturn(event: TolikWsPresenceReturn) {
                runOnUiThread {
                    TolikNotifier.notifyPresenceReturn(
                        this@MainActivity,
                        event.at?.hashCode() ?: event.summary?.hashCode() ?: 0,
                        event.summary ?: "",
                    )
                }
            }
        })
    }

    // ─── Confirmations (AN-2; the real bound-confirmation surface) ──────────

    private fun promptConfirmationForTask(taskId: String) {
        lifecycleScope.launch {
            val result = transport.listConfirmations(taskId, sessionStore.sessionToken)
            result.onSuccess { pending ->
                val open = pending.firstOrNull { it.approved != true }
                if (open != null) showConfirmationDialog(taskId, open)
            }
        }
    }

    private fun showConfirmationDialog(taskId: String, confirmation: TolikConfirmationInfo? = null) {
        val message = buildString {
            append(getString(R.string.confirmation_text))
            confirmation?.let {
                append("\n\n")
                append("capability: ${it.capability ?: "?"}\n")
                append("resource: ${it.resource ?: "?"}\n")
                append("risk: ${it.effectiveRisk ?: "?"}\n")
                append("method: ${it.requiredMethod ?: "-"}")
            }
        }
        AlertDialog.Builder(this)
            .setTitle(getString(R.string.confirmation_title))
            .setMessage(message)
            .setPositiveButton(R.string.confirmation_approve) { _, _ ->
                lifecycleScope.launch {
                    val id = confirmation?.id
                        ?: fetchPendingConfirmationId(taskId)
                    if (id == null) {
                        Toast.makeText(this@MainActivity, "no_pending_confirmation", Toast.LENGTH_LONG).show()
                        return@launch
                    }
                    val result = transport.approveConfirmation(taskId, id, sessionStore.sessionToken)
                    result.onSuccess {
                        Toast.makeText(this@MainActivity, it.note ?: "approved", Toast.LENGTH_LONG).show()
                    }
                    result.onFailure { failure ->
                        // 403 (owner role / MFA required) surfaces — never bypassed.
                        Toast.makeText(this@MainActivity, describeFailure(failure), Toast.LENGTH_LONG).show()
                    }
                }
            }
            .setNegativeButton(R.string.confirmation_deny) { dialog, _ ->
                // No deny endpoint exists on Core — deny = do not approve; the
                // task stays paused (or can be cancelled from the task list).
                dialog.dismiss()
            }
            .show()
    }

    private suspend fun fetchPendingConfirmationId(taskId: String): String? {
        val result = transport.listConfirmations(taskId, sessionStore.sessionToken)
        return result.getOrNull()?.firstOrNull { it.approved != true }?.id
    }

    // ─── Voice View implementation (marshals to main thread) ────────────────

    override fun onStateChanged(state: VoiceSessionController.State) = runOnUiThread {
        voiceState.text = when (state) {
            VoiceSessionController.State.IDLE -> getString(R.string.voice_idle)
            VoiceSessionController.State.LISTENING -> getString(R.string.voice_listening)
            VoiceSessionController.State.PROCESSING -> getString(R.string.voice_processing)
            VoiceSessionController.State.SPEAKING -> getString(R.string.voice_speaking)
        }
        voiceToggleButton.text = if (state == VoiceSessionController.State.IDLE) {
            getString(R.string.voice_start)
        } else {
            getString(R.string.voice_cancel)
        }
        voiceCancelButton.visibility =
            if (state == VoiceSessionController.State.IDLE) View.GONE else View.VISIBLE
    }

    override fun onPartialReply(text: String) = runOnUiThread {
        voiceReply.text = text
    }

    override fun onTranscript(text: String) = runOnUiThread {
        voiceTranscript.text = text
    }

    @SuppressLint("SetTextI18n")
    override fun onSpeakerHint(tier: String?, confidence: Double?) = runOnUiThread {
        // §13: probabilistic hint ONLY — displayed, never used for authorization.
        if (tier == null || tier == "none") {
            voiceSpeakerHint.visibility = View.GONE
        } else {
            voiceSpeakerHint.visibility = View.VISIBLE
            voiceSpeakerHint.text = getString(R.string.voice_speaker_hint_prefix) +
                "$tier (${confidence ?: 0.0})"
        }
    }

    override fun onError(message: String) = runOnUiThread {
        if (message.startsWith("auth_error")) {
            Toast.makeText(this, getString(R.string.chat_auth_error), Toast.LENGTH_LONG).show()
        } else {
            Toast.makeText(this, getString(R.string.chat_error_prefix) + message, Toast.LENGTH_LONG).show()
        }
        onStateChanged(voice.currentState)
    }

    // ─── Runtime permissions (never silent) ─────────────────────────────────

    private fun ensureMicPermission(): Boolean {
        if (ContextCompat.checkSelfPermission(this, Manifest.permission.RECORD_AUDIO) ==
            PackageManager.PERMISSION_GRANTED
        ) return true
        AlertDialog.Builder(this)
            .setTitle(R.string.tab_voice)
            .setMessage(R.string.voice_mic_permission_rationale)
            .setPositiveButton(R.string.ok) { _, _ ->
                requestPermissions(arrayOf(Manifest.permission.RECORD_AUDIO), REQUEST_MIC)
            }
            .setNegativeButton(R.string.cancel) { dialog, _ ->
                dialog.dismiss()
                Toast.makeText(this, R.string.voice_mic_permission_denied, Toast.LENGTH_LONG).show()
            }
            .show()
        return false
    }

    private fun maybeRequestNotifications() {
        if (TolikNotifier.canPost(this)) return
        if (shouldShowRequestPermissionRationale(Manifest.permission.POST_NOTIFICATIONS)) {
            AlertDialog.Builder(this)
                .setMessage(R.string.notifications_permission_rationale)
                .setPositiveButton(R.string.ok) { _, _ ->
                    requestPermissions(
                        arrayOf(TolikNotifier.runtimePermission),
                        REQUEST_NOTIFICATIONS,
                    )
                }
                .show()
        } else {
            requestPermissions(arrayOf(TolikNotifier.runtimePermission), REQUEST_NOTIFICATIONS)
        }
    }

    override fun onRequestPermissionsResult(
        requestCode: Int,
        permissions: Array<out String>,
        grantResults: IntArray,
    ) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        when (requestCode) {
            REQUEST_MIC -> if (grantResults.firstOrNull() == PackageManager.PERMISSION_GRANTED) {
                voice.startTurn()
            } else {
                Toast.makeText(this, R.string.voice_mic_permission_denied, Toast.LENGTH_LONG).show()
            }
            REQUEST_NOTIFICATIONS -> {
                // Denial is a choice — in-app task list still shows everything.
            }
        }
    }
}
