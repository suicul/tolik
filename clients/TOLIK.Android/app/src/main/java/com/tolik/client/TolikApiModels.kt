package com.tolik.client

// TOLIK Android thin client — contract layer (dependency-free).
//
// The client supplies a Core-issued session token per request; it contains no
// model credentials, agent permissions, memory, or authorization policy
// (AGENTS.md §36, SECURITY & TRUST MODEL §1). Core 401/403 responses are
// failures, never local authorization.
//
// Extensions below are additive: pre-existing data classes and the
// TolikApiTransport signatures are unchanged (new fields carry defaults,
// new members are appended).

data class TolikStatus(val status: String?, val role: String?, val userId: String?, val deviceId: String?)
data class TolikChatRequest(val message: String)

// Core /chat responses carry the reply in `answer` (Program.cs /chat).
// `response`/`message` predate the real contract and are kept for
// compatibility; `text` resolves whichever is present.
data class TolikChatResponse(
    val response: String?,
    val message: String?,
    val status: String?,
    val answer: String? = null,
    val model: String? = null,
    val source: String? = null,
) {
    val text: String? get() = answer ?: response ?: message
}

data class TolikTaskRequest(val description: String)

// Core task shape (TolikTask record / TolikTaskEntity, camelCase JSON).
// Timestamps are additive with defaults; existing 3-arg construction still compiles.
data class TolikTask(
    val id: String,
    val description: String?,
    val status: String?,
    val createdAt: String? = null,
    val completedAt: String? = null,
    val cancelledAt: String? = null,
)

data class TolikConfirmation(val confirmed: Boolean, val status: String?, val error: String?)
data class TolikApiError(val statusCode: Int, val error: String?)

/** Failure type used by transports: Core non-2xx surfaced, never retried silently. */
class TolikApiException(val statusCode: Int, val apiError: String?) :
    Exception("http_$statusCode: ${apiError ?: "unknown"}") {
    val isAuthError: Boolean get() = statusCode == 401 || statusCode == 403
}

interface TolikApiTransport {
    suspend fun getStatus(sessionToken: String?): Result<TolikStatus>
    suspend fun chat(request: TolikChatRequest, sessionToken: String?): Result<TolikChatResponse>
    suspend fun createTask(request: TolikTaskRequest, sessionToken: String?): Result<TolikTask>
    suspend fun getTask(taskId: String, sessionToken: String?): Result<TolikTask>
    suspend fun cancelTask(taskId: String, sessionToken: String?): Result<TolikTask>
    suspend fun confirmTask(taskId: String, sessionToken: String?): Result<TolikConfirmation>
}

// ─── Additive: session bootstrap (no token required — these MINT tokens) ─────

data class TolikSessionTokenResponse(
    val sessionToken: String?,
    val sessionId: String?,
    val expiresAt: String?,
)

// POST /auth/enroll — enrollment token minted by the Owner on an authorized
// client (/auth/enrollment-token). Explicit, role-scoped, expiring.
data class TolikEnrollRequest(
    val enrollmentToken: String,
    val installationId: String,
    val publicKey: String,
)

data class TolikEnrollResponse(
    val userId: String?,
    val deviceId: String?,
    val role: String?,
    val sessionId: String?,
    val sessionToken: String?,
    val expiresAt: String?,
)

// ─── Additive: status / health / agents ──────────────────────────────────────

// GET /health (unauthenticated on Core).
data class TolikHealth(
    val core: Boolean?,
    val voice: Boolean?,
    val agent: Boolean?,
    val memory: Boolean?,
    val component: String?,
    val authority: String?,
    val execution: String?,
    val locked: Boolean?,
    val coreLease: TolikCoreLease? = null,
)

data class TolikCoreLease(val stale: Boolean?, val reason: String?, val epoch: Long?, val host: String?)

// GET /agents — AgentConnection records (camelCase via JsonStringEnumConverter).
data class TolikAgentInfo(
    val agentId: String?,
    val capabilities: List<String>? = null,
    val lastSeen: String? = null,
    val status: String? = null,
)

// ─── Additive: confirmations (the actual Core confirmation surface) ──────────
// Core has NO /tasks/{id}/confirm route: pending confirmations are listed via
// GET /tasks/{id}/confirmations and approved one-by-one via
// POST /tasks/{id}/confirmations/{confirmationId}/approve. The pre-existing
// confirmTask(taskId) transport method is implemented as list + approve-first.

data class TolikConfirmationInfo(
    val id: String,
    val stepNumber: Int? = null,
    val capability: String? = null,
    val resource: String? = null,
    val approved: Boolean? = null,
    val effectiveRisk: String? = null,
    val requiredMethod: String? = null,
    val expiresAt: String? = null,
)

data class TolikApproveResponse(
    val approved: Boolean?,
    val confirmationId: String?,
    val note: String? = null,
)

// ─── Additive: voice (docs/voice-runtime-v2.md M4/M5/M6) ─────────────────────
// POST /voice/turn-stream — JSON body { audio (base64 WAV bytes), mode,
// runtimeMode }. Response is EITHER application/json (intent path →
// TolikVoiceTurnResponse) OR text/event-stream (model path): SSE events
// {type:"delta",text}, {type:"audio",audio:<base64>}, {type:"done"},
// {type:"error",error}. Core byte[] serializes as base64 in JSON.

data class TolikVoiceTurnRequest(
    val audio: String,          // base64 WAV (16 kHz mono PCM16) — Core byte[] binding
    val mode: String = "Normal",
    val runtimeMode: String = "Normal",
)

data class TolikVoiceTurnResponse(
    val transcription: String? = null,
    val replyText: String? = null,
    val mode: String? = null,
    val audio: String? = null, // base64 reply audio (may be null — text-only)
    val contentType: String? = null,
    val model: String? = null,
    val speakerVerified: Boolean? = null,
    val speakerConfidence: Double? = null,
    val speakerTier: String? = null,
    val speakerReason: String? = null,
)

data class TolikVoiceCancelResponse(val cancelled: Boolean?)

// One SSE event from /voice/turn-stream (lenient parse).
data class TolikVoiceStreamEvent(
    val type: String?,   // "delta" | "audio" | "done" | "error"
    val text: String? = null,
    val audio: String? = null, // base64 chunk
    val error: String? = null,
)

// ─── Additive: realtime event stream (GET /events/ws?token=…) ────────────────
// Server sends {"type":"hello","userId":…} and {"type":"event","payload":{…}}.
// Task payloads: { taskId, kind, detail, occurredAt } with kind ∈
// TaskStarted/TaskProgress/TaskCompleted/TaskFailed/TaskCancelled/TaskResumed/
// TaskPlanAttached/TaskJournal/TaskCheckpoint. Proactive reminder payloads:
// { type:"proactive_reminder", id, text, priority, dueAt, reason }.
// Expression payloads (Core 4def5df, ~5 Hz on change): { kind:
// "cephalon_expression", expression:{…16 bounded fields…, mode}, at }.
// Parsed with the framework org.json parser — this file stays dependency-free.

data class TolikWsTaskEvent(val taskId: String, val kind: String?, val detail: String?, val occurredAt: String?)

data class TolikWsProactive(val id: String?, val text: String?, val priority: String?, val dueAt: String?, val reason: String?)

// ─── Additive transport surface (extends TolikApiTransport; additive only) ───

/** Listener for the SSE voice turn stream. */
interface TolikVoiceStreamListener {
    /** Response was the JSON (intent) path — one full VoiceTurnResponse. */
    fun onJsonResponse(response: TolikVoiceTurnResponse)
    /** LLM text delta. */
    fun onDelta(text: String)
    /** Base64 audio chunk (play sequentially). */
    fun onAudio(base64Audio: String)
    /** Terminal success (SSE {type:"done"} or stream end). */
    fun onDone()
    /** Terminal failure (transport error or 401/403 — surfaced, never retried silently). */
    fun onError(error: Throwable)
}

/** Cancel handle for an in-flight voice turn stream (barge-in / M5). */
interface TolikVoiceStreamHandle {
    /** Aborts the HTTP request; the controller also POSTs /voice/cancel. */
    fun abort()
    val isDone: Boolean
}

/** Listener for GET /events/ws (RealtimeEventHub). */
interface TolikEventsListener {
    fun onConnected()
    fun onDisconnected(clean: Boolean, cause: Throwable?)
    fun onTaskEvent(event: TolikWsTaskEvent)
    fun onProactive(event: TolikWsProactive)

    /** Additive (AN-P2): a normalized cephalon_expression event arrived from
     * Core. Malformed/unknown payloads never reach this — the transport
     * ignores them safely. The expression is DATA to render, never authority
     * (§23). Default no-op so pre-existing implementations stay source-compatible. */
    fun onCephalonExpression(expression: ExpressionOutput) {}
}

interface TolikEventsStreamHandle {
    fun close()
}

interface TolikExtendedTransport {
    // Session bootstrap
    suspend fun getLocalDevToken(): Result<TolikSessionTokenResponse>
    suspend fun enroll(request: TolikEnrollRequest): Result<TolikEnrollResponse>

    // Status / agents
    suspend fun getHealth(): Result<TolikHealth>
    suspend fun listAgents(sessionToken: String?): Result<List<TolikAgentInfo>>

    // Confirmations (real Core surface)
    suspend fun listConfirmations(taskId: String, sessionToken: String?): Result<List<TolikConfirmationInfo>>
    suspend fun approveConfirmation(taskId: String, confirmationId: String, sessionToken: String?): Result<TolikApproveResponse>

    // Voice
    suspend fun cancelVoiceTurn(sessionToken: String?): Result<TolikVoiceCancelResponse>
    fun streamVoiceTurn(request: TolikVoiceTurnRequest, sessionToken: String?, listener: TolikVoiceStreamListener): TolikVoiceStreamHandle

    // Realtime
    fun openEvents(sessionToken: String, listener: TolikEventsListener): TolikEventsStreamHandle
}
