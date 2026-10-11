package com.tolik.client

import com.google.gson.Gson
import com.google.gson.JsonParser
import com.google.gson.reflect.TypeToken
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.Call
import okhttp3.Callback
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import org.json.JSONObject
import java.io.IOException
import java.util.concurrent.atomic.AtomicBoolean
import kotlin.math.min
import kotlin.random.Random

/**
 * OkHttp implementation of [TolikApiTransport] + [TolikExtendedTransport].
 *
 * Security invariants (SECURITY & TRUST MODEL):
 *  - Core 401/403 are ALWAYS surfaced as Result.failure — no local
 *    authorization, no silent retry loops on auth failures.
 *  - The session token is sent per request as a Bearer header; nothing else
 *    authenticates this client (network location is not authorization, §37).
 *  - No credentials, memory, or policy live in this class.
 *
 * Contract note: Core has no /tasks/{id}/confirm route — [confirmTask] is the
 * composite "list confirmations → approve the first pending one" over the two
 * real endpoints (GET /tasks/{id}/confirmations, POST …/{id}/approve).
 */
class OkHttpTolikTransport(
    baseUrl: String,
    client: OkHttpClient? = null,
    private val gson: Gson = Gson(),
) : TolikApiTransport, TolikExtendedTransport {

    private val base: String = baseUrl.trim().trimEnd('/')
    private val http: OkHttpClient = client ?: OkHttpClient.Builder().build()
    private val jsonMediaType = "application/json; charset=utf-8".toMediaType()

    // ─── Core plumbing ──────────────────────────────────────────────────────

    private fun url(path: String): String = base + "/" + path.trimStart('/')

    private fun bearer(sessionToken: String?): String? =
        sessionToken?.takeIf { it.isNotBlank() }?.let { "Bearer $it" }

    private fun body(payload: String) = payload.toRequestBody(jsonMediaType)

    private suspend fun execute(request: Request): Response = withContext(Dispatchers.IO) {
        http.newCall(request).execute()
    }

    /** Non-2xx → Result.failure; 401/403 mapped to explicit TolikApiException. */
    private suspend fun <T> call(request: Request, parse: (String) -> T): Result<T> {
        val response = try {
            execute(request)
        } catch (e: IOException) {
            return Result.failure(e)
        }
        response.use { resp ->
            val bodyText = try {
                resp.body?.string() ?: ""
            } catch (e: IOException) {
                return Result.failure(e)
            }
            if (!resp.isSuccessful) return Result.failure(errorFor(resp.code, bodyText))
            return try {
                Result.success(parse(bodyText))
            } catch (e: Exception) {
                Result.failure(e)
            }
        }
    }

    private fun errorFor(statusCode: Int, body: String): TolikApiException {
        val serverError = try {
            if (body.isBlank()) null
            else JsonParser.parseString(body)
                .takeIf { it.isJsonObject }
                ?.asJsonObject?.get("error")
                ?.takeIf { !it.isJsonNull }?.asString
        } catch (_: Exception) {
            null
        }
        val detail = serverError ?: when (statusCode) {
            401 -> "authentication_required"
            403 -> "authorization_denied"
            404 -> "not_found"
            409 -> "conflict"
            else -> "http_$statusCode"
        }
        return TolikApiException(statusCode, detail)
    }

    /** Gson parser for a single JSON object of [T]. */
    private inline fun <reified T> jsonParser(): (String) -> T = { body ->
        if (body.isBlank()) throw IOException("empty_response")
        gson.fromJson<T>(body, object : TypeToken<T>() {}.type)
    }

    /** Gson parser for a JSON array of [T]; blank/null bodies yield empty lists. */
    private inline fun <reified T> listParser(): (String) -> List<T> = { body ->
        if (body.isBlank()) {
            emptyList()
        } else {
            val element = JsonParser.parseString(body)
            if (element.isJsonNull || !element.isJsonArray) {
                emptyList()
            } else {
                val type = object : TypeToken<List<T>>() {}.type
                gson.fromJson<List<T>>(body, type)
            }
        }
    }

    // ─── TolikApiTransport (signatures preserved) ───────────────────────────

    override suspend fun getStatus(sessionToken: String?): Result<TolikStatus> {
        val builder = Request.Builder().url(url("/status"))
        bearer(sessionToken)?.let { builder.header("Authorization", it) }
        return call(builder.build(), jsonParser())
    }

    override suspend fun chat(request: TolikChatRequest, sessionToken: String?): Result<TolikChatResponse> {
        val builder = Request.Builder()
            .url(url("/chat"))
            .post(body(gson.toJson(request)))
        bearer(sessionToken)?.let { builder.header("Authorization", it) }
        return call(builder.build(), jsonParser())
    }

    override suspend fun createTask(request: TolikTaskRequest, sessionToken: String?): Result<TolikTask> {
        val builder = Request.Builder()
            .url(url("/tasks"))
            .post(body(gson.toJson(request)))
        bearer(sessionToken)?.let { builder.header("Authorization", it) }
        return call(builder.build(), jsonParser())
    }

    override suspend fun getTask(taskId: String, sessionToken: String?): Result<TolikTask> {
        val builder = Request.Builder().url(url("/tasks/$taskId"))
        bearer(sessionToken)?.let { builder.header("Authorization", it) }
        return call(builder.build(), jsonParser())
    }

    override suspend fun cancelTask(taskId: String, sessionToken: String?): Result<TolikTask> {
        val builder = Request.Builder().url(url("/tasks/$taskId/cancel"))
            .post(body(""))
        bearer(sessionToken)?.let { builder.header("Authorization", it) }
        return call(builder.build(), jsonParser())
    }

    override suspend fun confirmTask(taskId: String, sessionToken: String?): Result<TolikConfirmation> {
        // Composite over the real confirmation surface (see class KDoc).
        val pending = listConfirmations(taskId, sessionToken)
        if (pending.isFailure) {
            return Result.failure(pending.exceptionOrNull() ?: IOException("confirmations_unavailable"))
        }
        val confirmation = pending.getOrThrow().firstOrNull { it.approved != true }
            ?: return Result.failure(IllegalStateException("no_pending_confirmation"))
        val approve = approveConfirmation(taskId, confirmation.id, sessionToken)
        if (approve.isFailure) {
            return Result.failure(approve.exceptionOrNull() ?: IOException("approve_failed"))
        }
        return Result.success(
            TolikConfirmation(
                confirmed = approve.getOrThrow().approved == true,
                status = "approved",
                error = null,
            )
        )
    }

    // ─── TolikExtendedTransport: session bootstrap ──────────────────────────

    override suspend fun getLocalDevToken(): Result<TolikSessionTokenResponse> {
        // GET /auth/local-token — dev-only on Core (TOLIK_DEV_AUTO_TOKEN +
        // loopback, blocked after Genesis). Otherwise 404 — surfaced as failure.
        return call(Request.Builder().url(url("/auth/local-token")).get().build(), jsonParser())
    }

    override suspend fun enroll(request: TolikEnrollRequest): Result<TolikEnrollResponse> {
        return call(
            Request.Builder()
                .url(url("/auth/enroll"))
                .post(body(gson.toJson(request)))
                .build(),
            jsonParser(),
        )
    }

    // ─── TolikExtendedTransport: status / agents ────────────────────────────

    override suspend fun getHealth(): Result<TolikHealth> {
        // /health is intentionally unauthenticated on Core.
        return call(Request.Builder().url(url("/health")).get().build(), jsonParser())
    }

    override suspend fun listAgents(sessionToken: String?): Result<List<TolikAgentInfo>> {
        val builder = Request.Builder().url(url("/agents"))
        bearer(sessionToken)?.let { builder.header("Authorization", it) }
        return call(builder.build(), listParser())
    }

    // ─── TolikExtendedTransport: confirmations ──────────────────────────────

    override suspend fun listConfirmations(taskId: String, sessionToken: String?): Result<List<TolikConfirmationInfo>> {
        val builder = Request.Builder().url(url("/tasks/$taskId/confirmations"))
        bearer(sessionToken)?.let { builder.header("Authorization", it) }
        return call(builder.build(), listParser())
    }

    override suspend fun approveConfirmation(
        taskId: String,
        confirmationId: String,
        sessionToken: String?,
    ): Result<TolikApproveResponse> {
        val builder = Request.Builder()
            .url(url("/tasks/$taskId/confirmations/$confirmationId/approve"))
            .post(body(""))
        bearer(sessionToken)?.let { builder.header("Authorization", it) }
        return call(builder.build(), jsonParser())
    }

    // ─── TolikExtendedTransport: voice (voice-runtime-v2.md M4/M5) ──────────

    override suspend fun cancelVoiceTurn(sessionToken: String?): Result<TolikVoiceCancelResponse> {
        val builder = Request.Builder().url(url("/voice/cancel")).post(body(""))
        bearer(sessionToken)?.let { builder.header("Authorization", it) }
        return call(builder.build(), jsonParser())
    }

    override fun streamVoiceTurn(
        request: TolikVoiceTurnRequest,
        sessionToken: String?,
        listener: TolikVoiceStreamListener,
    ): TolikVoiceStreamHandle {
        val builder = Request.Builder()
            .url(url("/voice/turn-stream"))
            .post(body(gson.toJson(request)))
        bearer(sessionToken)?.let { builder.header("Authorization", it) }

        val call = http.newCall(builder.build())
        val aborted = AtomicBoolean(false)
        val finished = AtomicBoolean(false)

        call.enqueue(object : Callback {
            override fun onFailure(call: Call, e: IOException) {
                if (aborted.get()) {
                    // Barge-in abort is deliberate — not an error surface.
                    finished.set(true)
                    listener.onDone()
                    return
                }
                finished.set(true)
                listener.onError(e)
            }

            override fun onResponse(call: Call, response: Response) {
                response.use { resp ->
                    try {
                        if (!resp.isSuccessful) {
                            val bodyText = try {
                                resp.body?.string().orEmpty()
                            } catch (_: IOException) {
                                ""
                            }
                            finished.set(true)
                            listener.onError(errorFor(resp.code, bodyText))
                            return
                        }
                        val contentType = resp.header("Content-Type").orEmpty()
                        if (contentType.contains("application/json", ignoreCase = true)) {
                            // Intent path: the same JSON as /voice/turn.
                            val bodyText = try {
                                resp.body?.string().orEmpty()
                            } catch (_: IOException) {
                                ""
                            }
                            finished.set(true)
                            val parsed = if (bodyText.isBlank()) {
                                TolikVoiceTurnResponse()
                            } else {
                                try {
                                    gson.fromJson(bodyText, TolikVoiceTurnResponse::class.java)
                                } catch (_: Exception) {
                                    TolikVoiceTurnResponse()
                                }
                            }
                            listener.onJsonResponse(parsed)
                            listener.onDone()
                            return
                        }
                        // Model path: text/event-stream — "data: {json}\n\n" lines.
                        val source = resp.body?.source()
                        if (source == null) {
                            finished.set(true)
                            listener.onDone()
                            return
                        }
                        while (!aborted.get()) {
                            val line = try {
                                source.readUtf8Line() ?: break
                            } catch (e: IOException) {
                                if (aborted.get()) break
                                finished.set(true)
                                listener.onError(e)
                                return
                            }
                            if (aborted.get()) break
                            if (!line.startsWith("data:")) continue
                            val payload = line.removePrefix("data:").trim()
                            if (payload.isEmpty()) continue
                            val event = parseVoiceEvent(payload)
                            when (event?.type) {
                                "delta" -> event.text?.let { listener.onDelta(it) }
                                "audio" -> event.audio?.let { listener.onAudio(it) }
                                "done" -> {
                                    finished.set(true)
                                    listener.onDone()
                                    return
                                }
                                "error" -> {
                                    finished.set(true)
                                    listener.onError(IOException("voice_stream_error:${event.error ?: "unknown"}"))
                                    return
                                }
                            }
                        }
                        // Stream ended without an explicit done (server cancelled).
                        if (finished.compareAndSet(false, true)) listener.onDone()
                    } finally {
                        finished.set(true)
                    }
                }
            }
        })

        return object : TolikVoiceStreamHandle {
            override fun abort() {
                aborted.set(true)
                call.cancel()
            }
            override val isDone: Boolean get() = finished.get()
        }
    }

    private fun parseVoiceEvent(payload: String): TolikVoiceStreamEvent? = try {
        val obj = JSONObject(payload)
        TolikVoiceStreamEvent(
            type = obj.optStringOrNull("type"),
            text = obj.optStringOrNull("text"),
            audio = obj.optStringOrNull("audio"),
            error = obj.optStringOrNull("error"),
        )
    } catch (_: Exception) {
        null
    }

    // ─── TolikExtendedTransport: realtime events (GET /events/ws?token=…) ───

    override fun openEvents(sessionToken: String, listener: TolikEventsListener): TolikEventsStreamHandle {
        return TolikEventSocket(http, eventsWebSocketUrl(base, sessionToken), listener)
    }
}

/**
 * Builds the /events/ws URL with the session token percent-encoded as a query
 * value. The token is Base64 and CAN contain '+' and '/' — a raw '+' decodes as
 * a space server-side and the handshake 401s (verified live against the running
 * Core; the same rule as Uri.EscapeDataString). Pure JVM — production-tested.
 */
internal fun eventsWebSocketUrl(baseUrl: String, sessionToken: String): String {
    val encoded = try {
        java.net.URLEncoder.encode(sessionToken, "UTF-8")
    } catch (_: Exception) {
        sessionToken // never happens for UTF-8; fail soft to prior behavior
    }
    return baseUrl.trim().trimEnd('/') + "/events/ws?token=$encoded"
}

/** OkHttp WebSocket for /events/ws with bounded reconnect backoff. */
private class TolikEventSocket(
    private val client: OkHttpClient,
    private val wsUrl: String,
    private val listener: TolikEventsListener,
) : TolikEventsStreamHandle, WebSocketListener() {

    private val closedByClient = AtomicBoolean(false)
    private var socket: WebSocket? = null
    private var attempt = 0

    init {
        connect()
    }

    private fun connect() {
        if (closedByClient.get()) return
        val request = Request.Builder().url(wsUrl).build()
        socket = client.newWebSocket(request, this)
    }

    override fun onOpen(webSocket: WebSocket, response: Response) {
        attempt = 0
        listener.onConnected()
    }

    override fun onMessage(webSocket: WebSocket, text: String) {
        // Server frames: {"type":"hello","userId":…} and
        // {"type":"event","payload":{…}} — payloads are untrusted data
        // (§22/§23): they refresh views, they never grant anything.
        // cephalon_expression frames route first (pure parser → normalized
        // ExpressionOutput); every other frame falls through to the org.json
        // path below. Malformed/unknown frames are ignored safely by both.
        val expression = CephalonWireParser.parseFrame(text)
        if (expression != null) {
            listener.onCephalonExpression(expression)
            return
        }
        val obj = try {
            JSONObject(text)
        } catch (_: Exception) {
            return
        }
        when (obj.optStringOrNull("type")) {
            "hello" -> listener.onConnected()
            "event" -> {
                val payload = obj.optJSONObject("payload") ?: return
                val taskId = payload.optStringOrNull("taskId")
                if (taskId != null) {
                    listener.onTaskEvent(
                        TolikWsTaskEvent(
                            taskId = taskId,
                            kind = payload.optStringOrNull("kind"),
                            detail = payload.optStringOrNull("detail"),
                            occurredAt = payload.optStringOrNull("occurredAt"),
                        )
                    )
                }
                if (payload.optStringOrNull("type") == "proactive_reminder") {
                    listener.onProactive(
                        TolikWsProactive(
                            id = payload.optStringOrNull("id"),
                            text = payload.optStringOrNull("text"),
                            priority = payload.optStringOrNull("priority"),
                            dueAt = payload.optStringOrNull("dueAt"),
                            reason = payload.optStringOrNull("reason"),
                        )
                    )
                }
            }
        }
    }

    override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
        listener.onDisconnected(clean = true, cause = null)
        scheduleReconnect()
    }

    override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
        // A 401 during the handshake is an auth failure: surface it, do NOT
        // retry-forever — reconnect happens only for non-auth failures and
        // clean closes, with bounded backoff (§14: fail closed, no loops).
        val unauthorized = response?.code == 401
        // Surface auth failures explicitly (§14 honest fail): the caller can
        // distinguish a 401 handshake from a network drop — never silent,
        // never fake-idle-pretending-connected.
        val cause = if (unauthorized) {
            TolikApiException(401, "authentication_required")
        } else {
            t
        }
        listener.onDisconnected(clean = false, cause = cause)
        if (!unauthorized) scheduleReconnect()
    }

    private fun scheduleReconnect() {
        if (closedByClient.get()) return
        // Exponential backoff with jitter, capped at 30s.
        val baseDelay = min(30_000L, 500L * (1L shl min(attempt, 6)))
        attempt++
        val delay = baseDelay + Random.nextLong(0, 500)
        android.os.Handler(android.os.Looper.getMainLooper()).postDelayed(
            { connect() },
            delay,
        )
    }

    override fun close() {
        closedByClient.set(true)
        socket?.close(1000, "client_shutdown")
        socket = null
    }
}

/** org.json helper: null when the key is missing or JSON null. */
internal fun JSONObject.optStringOrNull(key: String): String? {
    if (!has(key)) return null
    val value = opt(key) ?: return null
    if (value === JSONObject.NULL) return null
    return value.toString()
}
