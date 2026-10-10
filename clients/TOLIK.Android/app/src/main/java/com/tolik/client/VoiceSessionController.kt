package com.tolik.client

import android.util.Base64
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.launch

/**
 * Voice turn orchestration for the thin client. Owns NO authorization, NO
 * memory, NO model credentials — it shapes local audio into a /voice/turn-stream
 * request (voice-runtime-v2.md M4), plays SSE audio chunks sequentially (M4b),
 * and implements barge-in as abort + POST /voice/cancel (M5, ESC-analog).
 *
 * Turn-level echo gate (0.10 live fix): the mic NEVER captures while TOLIK
 * audio is playing, and a new capture is refused for [VoiceClientDefaults.ECHO_TAIL_MS]
 * after playback ends — this is what killed the desktop echo loop. Voice
 * barge-in during playback needs AEC and is deliberately not faked: the
 * visible cancel button is the barge-in path.
 *
 * Speaker verification results arriving in turn responses are a probabilistic
 * hint (§13) surfaced to the UI — never a local authorization decision.
 */
class VoiceSessionController(
    private val transport: OkHttpTolikTransport,
    private val player: TtsChunkPlayer,
    private val scope: CoroutineScope,
    private val sessionToken: () -> String?,
) : TtsChunkPlayer.Listener {

    enum class State { IDLE, LISTENING, PROCESSING, SPEAKING }

    /** UI callback (main thread by contract with MainActivity). */
    interface View {
        fun onStateChanged(state: State)
        fun onPartialReply(text: String)
        fun onTranscript(text: String)
        fun onSpeakerHint(tier: String?, confidence: Double?)
        fun onError(message: String)
    }

    var view: View? = null

    private var state: State = State.IDLE
    private var mic: MicCaptureEngine? = null
    private var streamHandle: TolikVoiceStreamHandle? = null
    private var replyBuilder = StringBuilder()
    private var playbackEndedAt = 0L

    val currentState: State get() = state

    init {
        player.setListener(this)
    }

    // ─── Public controls ─────────────────────────────────────────────────────

    /**
     * Starts a voice turn: if TOLIK is speaking/processing, this is a barge-in
     * first (cancel button semantics); then mic capture opens. The caller must
     * have RECORD_AUDIO granted.
     */
    @Synchronized
    fun startTurn() {
        if (state == State.SPEAKING || state == State.PROCESSING) {
            bargeIn()
        }
        // Turn-level echo gate: refuse capture during the post-playback tail.
        if (System.currentTimeMillis() - playbackEndedAt < VoiceClientDefaults.ECHO_TAIL_MS) {
            return
        }
        if (state == State.LISTENING) return
        replyBuilder = StringBuilder()
        view?.onPartialReply("")
        view?.onTranscript("")
        view?.onSpeakerHint(null, null)
        val engine = MicCaptureEngine(
            onUtterance = { utterance -> onUtteranceCaptured(utterance) },
            onStateChange = { capturing ->
                if (capturing && state == State.LISTENING) view?.onStateChanged(State.LISTENING)
            },
        )
        try {
            engine.start()
        } catch (e: Exception) {
            view?.onError(e.message ?: "audio_record_unavailable")
            setState(State.IDLE)
            return
        }
        mic = engine
        setState(State.LISTENING)
    }

    /** Stops an open capture without sending anything. */
    @Synchronized
    fun stopListening() {
        mic?.stop()
        mic = null
        if (state == State.LISTENING) setState(State.IDLE)
    }

    /** Barge-in (M5): abort the stream, stop playback, POST /voice/cancel. */
    @Synchronized
    fun bargeIn() {
        streamHandle?.abort()
        streamHandle = null
        player.stopAll()
        mic?.stop()
        mic = null
        val token = sessionToken()
        scope.launch {
            // Failures are surfaced, never silent (§40).
            val result = transport.cancelVoiceTurn(token)
            if (result.isFailure) {
                view?.onError("voice_cancel_failed: ${result.exceptionOrNull()?.message ?: "unknown"}")
            }
        }
        setState(State.IDLE)
    }

    /** Teardown (Activity destroy). */
    fun release() {
        streamHandle?.abort()
        streamHandle = null
        mic?.stop()
        mic = null
        player.stopAll()
    }

    // ─── Internals ───────────────────────────────────────────────────────────

    private fun onUtteranceCaptured(utterance: Utterance) {
        synchronized(this) {
            mic?.stop()
            mic = null
            // M6 MIN_SPEECH_MS mirror: below 250 ms of speech it is not a turn.
            if (utterance.speechMs < VoiceClientDefaults.MIN_SPEECH_MS) {
                setState(State.IDLE)
                return
            }
            if (state != State.LISTENING) {
                setState(State.IDLE)
                return
            }
            setState(State.PROCESSING)
        }
        val wav = WavWriter.wavFromPcm(utterance.pcm)
        val audioBase64 = Base64.encodeToString(wav, Base64.NO_WRAP)
        val token = sessionToken()
        val controller = this
        streamHandle = transport.streamVoiceTurn(
            TolikVoiceTurnRequest(audio = audioBase64),
            token,
            object : TolikVoiceStreamListener {
                override fun onJsonResponse(response: TolikVoiceTurnResponse) {
                    val text = response.replyText ?: response.transcription.orEmpty()
                    controller.view?.onTranscript(response.transcription.orEmpty())
                    controller.view?.onPartialReply(text)
                    // §13: hint only — displayed, never evaluated locally.
                    controller.view?.onSpeakerHint(response.speakerTier, response.speakerConfidence)
                    response.audio?.let { player.enqueue(it) }
                }

                override fun onDelta(text: String) {
                    replyBuilder.append(text)
                    controller.view?.onPartialReply(replyBuilder.toString())
                }

                override fun onAudio(base64Audio: String) {
                    player.enqueue(base64Audio)
                }

                override fun onDone() {
                    controller.streamHandle = null
                    if (!player.isPlaying) controller.setState(State.IDLE)
                }

                override fun onError(error: Throwable) {
                    controller.streamHandle = null
                    controller.view?.onError(describe(error))
                    controller.setState(State.IDLE)
                }
            },
        )
    }

    // ─── TtsChunkPlayer.Listener (echo gate state) ───────────────────────────

    override fun onPlaybackStarted() {
        if (state == State.IDLE || state == State.PROCESSING) setState(State.SPEAKING)
    }

    override fun onPlaybackEnded() {
        playbackEndedAt = System.currentTimeMillis()
        if (state == State.SPEAKING) setState(State.IDLE)
    }

    private fun setState(next: State) {
        state = next
        view?.onStateChanged(next)
    }

    private fun describe(error: Throwable): String = when {
        error is TolikApiException && error.isAuthError -> "auth_error:${error.statusCode}"
        error.message?.startsWith("voice_stream_error:") == true -> error.message ?: "voice_error"
        else -> "voice_error: ${error.message ?: error.javaClass.simpleName}"
    }
}
