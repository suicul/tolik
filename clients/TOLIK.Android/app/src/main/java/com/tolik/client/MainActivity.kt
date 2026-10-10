package com.tolik.client

import android.Manifest
import android.annotation.SuppressLint
import android.content.pm.PackageManager
import android.os.Bundle
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
import kotlinx.coroutines.launch

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

    private var eventsHandle: TolikEventsStreamHandle? = null

    private companion object {
        const val REQUEST_MIC = 41
        const val REQUEST_NOTIFICATIONS = 42
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
        showScreen(R.id.screen_chat)
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
    }

    private fun showScreen(id: Int) {
        screenChat.visibility = if (id == R.id.screen_chat) View.VISIBLE else View.GONE
        screenVoice.visibility = if (id == R.id.screen_voice) View.VISIBLE else View.GONE
        screenTasks.visibility = if (id == R.id.screen_tasks) View.VISIBLE else View.GONE
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
                runOnUiThread { refreshStatus() }
            }

            override fun onDisconnected(clean: Boolean, cause: Throwable?) {
                // Auth failures surface in the header (no retry — the socket
                // stops reconnecting on 401); non-auth reconnects are inside
                // the transport's bounded backoff.
                if (cause != null) {
                    runOnUiThread {
                        if (cause is TolikApiException) {
                            renderStatus(describeFailure(cause), connected = false)
                        }
                    }
                }
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
