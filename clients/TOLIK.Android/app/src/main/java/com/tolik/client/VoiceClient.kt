package com.tolik.client

import android.annotation.SuppressLint
import android.content.Context
import android.media.AudioFormat
import android.media.AudioRecord
import android.media.MediaPlayer
import android.media.MediaRecorder
import android.util.Base64
import java.io.File
import java.io.FileOutputStream
import java.util.ArrayDeque
import java.util.concurrent.atomic.AtomicBoolean

/**
 * Voice capture/playback for the thin Android client
 * (docs/voice-runtime-v2.md M4/M5/M6 + 0.10 direct-mic path).
 *
 * Mirrored semantics (from the doc — no invented behavior):
 *  - capture: 16 kHz mono PCM16 (M6 audio contract);
 *  - utterance shaping: pre-roll 250 ms, end-silence 700 ms, hard cap 10 s
 *    (0.10 UtteranceSegmenter, ported from src/TOLIK.Contracts);
 *  - min-speech filter: utterances with < 250 ms of speech are discarded
 *    (M6 MIN_SPEECH_MS);
 *  - turn-level echo gate: the mic is gated for the WHOLE turn plus a 400 ms
 *    tail after playback (0.10 live fix against the TTS echo loop);
 *  - spoken-sentence budget (cap 3) is enforced SERVER-side
 *    (VoiceResponsePolicy.MaxSpokenSentences) — the client never re-voices;
 *  - barge-in (M5): visible cancel button → abort SSE + POST /voice/cancel.
 *    Voice barge-in during TOLIK playback requires AEC (honest limitation,
 *    doc line "голосовой barge-in во время речи TOLIK требует AEC") — the
 *    button is the ESC-analog, so the mic is gated during playback.
 *
 * The client-side amplitude gate is a LOCAL capture shaper only
 * (UtteranceSegmenter KDoc: "this only shapes LOCAL audio into a turn
 * request"). Silero-grade VAD stays on the server (/vad/detect). Speaker
 * verification results are a probabilistic hint (§13) — never authorization.
 */

/** Wraps raw 16 kHz mono PCM16 bytes in a minimal 44-byte WAV header. */
object WavWriter {
    fun wavFromPcm(pcm: ByteArray, sampleRate: Int = 16_000, channels: Int = 1, bitsPerSample: Int = 16): ByteArray {
        val byteRate = sampleRate * channels * bitsPerSample / 8
        val blockAlign = channels * bitsPerSample / 8
        val totalLen = pcm.size + 36
        val out = ByteArray(44 + pcm.size)
        fun putInt(offset: Int, value: Int) {
            out[offset] = (value and 0xff).toByte()
            out[offset + 1] = ((value shr 8) and 0xff).toByte()
            out[offset + 2] = ((value shr 16) and 0xff).toByte()
            out[offset + 3] = ((value shr 24) and 0xff).toByte()
        }
        fun putShort(offset: Int, value: Int) {
            out[offset] = (value and 0xff).toByte()
            out[offset + 1] = ((value shr 8) and 0xff).toByte()
        }
        // "RIFF" + total size + "WAVE" + "fmt " + 16 + PCM + channels + rate
        // + byteRate + blockAlign + bits + "data" + data size
        out[0] = 'R'.code.toByte(); out[1] = 'I'.code.toByte(); out[2] = 'F'.code.toByte(); out[3] = 'F'.code.toByte()
        putInt(4, totalLen)
        out[8] = 'W'.code.toByte(); out[9] = 'A'.code.toByte(); out[10] = 'V'.code.toByte(); out[11] = 'E'.code.toByte()
        out[12] = 'f'.code.toByte(); out[13] = 'm'.code.toByte(); out[14] = 't'.code.toByte(); out[15] = ' '.code.toByte()
        putInt(16, 16)
        putShort(20, 1) // PCM
        putShort(22, channels)
        putInt(24, sampleRate)
        putInt(28, byteRate)
        putShort(32, blockAlign)
        putShort(34, bitsPerSample)
        out[36] = 'd'.code.toByte(); out[37] = 'a'.code.toByte(); out[38] = 't'.code.toByte(); out[39] = 'a'.code.toByte()
        putInt(40, pcm.size)
        System.arraycopy(pcm, 0, out, 44, pcm.size)
        return out
    }
}

/**
 * Amplitude gate: per-chunk RMS speech verdict — a rough local approximation of
 * the M6 gate (Silero lives server-side). Threshold default 0.02 (normalized
 * RMS); see [VoiceClientDefaults].
 */
class AmplitudeVadGate(private val threshold: Double = VoiceClientDefaults.AMPLITUDE_THRESHOLD) {
    /** Normalized RMS (0..1) of one PCM16 LE chunk. */
    fun rms(chunk: ByteArray): Double {
        if (chunk.size < 2) return 0.0
        var sum = 0.0
        var samples = 0
        var i = 0
        while (i + 1 < chunk.size) {
            val sample = ((chunk[i].toInt() and 0xff) or (chunk[i + 1].toInt() shl 8)).toShort().toDouble()
            sum += sample * sample
            samples++
            i += 2
        }
        if (samples == 0) return 0.0
        return minOf(1.0, kotlin.math.sqrt(sum / samples) / 32768.0)
    }

    fun hasSpeech(chunk: ByteArray): Boolean = rms(chunk) >= threshold
}

/**
 * Port of src/TOLIK.Contracts/UtteranceSegmenter.cs (0.10): pre-roll keeps the
 * silence before speech onset; end-silence closes the utterance; a hard cap
 * bounds runaway capture. Chunk sizes may vary; timing derives from byte counts.
 */
class UtteranceSegmenter(
    preRollMs: Int = 250,
    endSilenceMs: Int = 700,
    maxUtteranceMs: Int = 10_000,
    sampleRate: Int = 16_000,
    bytesPerSample: Int = 2,
) {
    private val bytesPerMs = sampleRate * bytesPerSample / 1000
    private val preRollBytes = preRollMs * bytesPerMs
    private val endSilenceBytes = endSilenceMs * bytesPerMs
    private val maxUtteranceBytes = maxUtteranceMs * bytesPerMs
    private val tail = ArrayDeque<ByteArray>() // recent silence before onset (pre-roll)
    private val utterance = ArrayList<ByteArray>()
    private var tailBytes = 0
    private var utteranceBytes = 0
    private var trailingSilenceBytes = 0
    private var capturing = false

    val isCapturing: Boolean get() = capturing

    /**
     * Feeds one mic chunk with its VAD verdict. Returns the complete utterance
     * (raw 16 kHz mono PCM, pre-roll included) when end-silence or the hard cap
     * closes it; null while the utterance is open or idle.
     */
    fun feed(chunk: ByteArray, hasSpeech: Boolean): ByteArray? {
        if (chunk.isEmpty()) return null

        if (!capturing) {
            if (!hasSpeech) {
                // Idle: keep only the last pre-roll worth of silence.
                tail.addLast(chunk)
                tailBytes += chunk.size
                while (tailBytes > preRollBytes) {
                    val overflow = tailBytes - preRollBytes
                    val front = tail.peekFirst() ?: break
                    if (front.size <= overflow) {
                        tailBytes -= front.size
                        tail.removeFirst()
                    } else {
                        val keep = front.size - overflow
                        val trimmed = front.copyOfRange(overflow, front.size)
                        tailBytes -= overflow
                        tail.removeFirst()
                        tail.addLast(trimmed)
                        break
                    }
                }
                return null
            }
            // Silence -> speech: the utterance opens with the pre-roll first.
            startCapture()
        }

        utterance.add(chunk)
        utteranceBytes += chunk.size
        if (hasSpeech) {
            trailingSilenceBytes = 0
        } else {
            trailingSilenceBytes += chunk.size
            if (trailingSilenceBytes >= endSilenceBytes) return complete()
        }
        if (utteranceBytes >= maxUtteranceBytes) return complete()
        return null
    }

    /** Closes the open utterance (capture stopped / mode switch). Null when idle. */
    fun flush(): ByteArray? = if (capturing) complete() else null

    private fun startCapture() {
        capturing = true
        utterance.clear()
        utteranceBytes = 0
        trailingSilenceBytes = 0
        for (pre in tail) {
            utterance.add(pre)
            utteranceBytes += pre.size
        }
        tail.clear()
        tailBytes = 0
    }

    private fun complete(): ByteArray {
        val result = ByteArray(utteranceBytes)
        var offset = 0
        for (part in utterance) {
            System.arraycopy(part, 0, result, offset, part.size)
            offset += part.size
        }
        capturing = false
        utterance.clear()
        utteranceBytes = 0
        trailingSilenceBytes = 0
        return result
    }
}

/** Shared knobs mirrored from voice-runtime-v2.md — single place, no magic numbers. */
object VoiceClientDefaults {
    const val SAMPLE_RATE = 16_000
    const val CHANNELS = 1
    const val BITS_PER_SAMPLE = 16
    const val CHUNK_MS = 40L

    // M6 TOLIK_VAD_* mirrors (threshold is the amplitude-gate approximation).
    const val AMPLITUDE_THRESHOLD = 0.02
    const val MIN_SPEECH_MS = 250

    // 0.10 UtteranceSegmenter defaults.
    const val PRE_ROLL_MS = 250
    const val END_SILENCE_MS = 700
    const val HARD_CAP_MS = 10_000

    // 0.10 live fix: turn-level echo gate tail after playback.
    const val ECHO_TAIL_MS = 400L
}

/** A complete utterance plus how much of it was detected as speech. */
class Utterance(val pcm: ByteArray, val speechMs: Int)

/**
 * Mic capture engine: AudioRecord 16 kHz mono PCM16 → amplitude gate →
 * utterance segmenter → complete-utterance callback. Runs on its own thread;
 * the caller must hold RECORD_AUDIO (runtime permission — never silent).
 */
class MicCaptureEngine(
    private val onUtterance: (Utterance) -> Unit,
    private val onStateChange: (Boolean) -> Unit, // capturing (speech open)
    private val gate: AmplitudeVadGate = AmplitudeVadGate(),
    private val segmenter: UtteranceSegmenter = UtteranceSegmenter(),
) {
    private val running = AtomicBoolean(false)
    private var thread: Thread? = null
    private var record: AudioRecord? = null
    /** Speech duration of the open utterance (M6 MIN_SPEECH_MS mirror): the mic
     * loop resets it when a new capture opens; stop() reads it after joining the
     * loop thread (the join gives happens-before) to wrap the still-open utterance. */
    @Volatile private var speechMs = 0

    val isRunning: Boolean get() = running.get()

    @SuppressLint("MissingPermission") // caller enforces the runtime permission
    fun start() {
        if (!running.compareAndSet(false, true)) return
        val minBuffer = AudioRecord.getMinBufferSize(
            VoiceClientDefaults.SAMPLE_RATE,
            AudioFormat.CHANNEL_IN_MONO,
            AudioFormat.ENCODING_PCM_16BIT,
        )
        val bufferSize = maxOf(minBuffer, VoiceClientDefaults.SAMPLE_RATE) // ≥ 1 s
        val audioRecord = try {
            AudioRecord(
                MediaRecorder.AudioSource.MIC,
                VoiceClientDefaults.SAMPLE_RATE,
                AudioFormat.CHANNEL_IN_MONO,
                AudioFormat.ENCODING_PCM_16BIT,
                bufferSize,
            )
        } catch (e: Exception) {
            running.set(false)
            onStateChange(false)
            throw IllegalStateException("audio_record_unavailable", e)
        }
        if (audioRecord.state != AudioRecord.STATE_INITIALIZED) {
            audioRecord.release()
            running.set(false)
            onStateChange(false)
            throw IllegalStateException("audio_record_unavailable")
        }
        record = audioRecord
        val worker = Thread({
            val chunkSize = (VoiceClientDefaults.SAMPLE_RATE * 2 * VoiceClientDefaults.CHUNK_MS / 1000).toInt()
            val chunkMs = VoiceClientDefaults.CHUNK_MS
            val chunk = ByteArray(chunkSize)
            var wasCapturing = false
            try {
                audioRecord.startRecording()
                while (running.get()) {
                    val read = audioRecord.read(chunk, 0, chunk.size)
                    if (read <= 0) continue
                    val data = if (read == chunk.size) chunk else chunk.copyOf(read)
                    val speech = gate.hasSpeech(data)
                    // Track speech duration per utterance (M6 MIN_SPEECH_MS mirror):
                    // the counter resets when the segmenter opens a new capture.
                    if (segmenter.isCapturing && !wasCapturing) speechMs = 0
                    wasCapturing = segmenter.isCapturing
                    if (speech && segmenter.isCapturing) speechMs += chunkMs.toInt()
                    val complete = segmenter.feed(data, speech)
                    if (complete != null) onUtterance(Utterance(complete, speechMs))
                    onStateChange(segmenter.isCapturing)
                }
            } finally {
                try {
                    audioRecord.stop()
                } catch (_: Exception) {
                }
                audioRecord.release()
            }
        }, "tolik-mic")
        thread = worker
        worker.start()
    }

    /** Stops capture; a still-open utterance is flushed to [onUtterance]. */
    fun stop() {
        if (!running.compareAndSet(true, false)) return
        try {
            thread?.interrupt()
            thread?.join(1000)
        } catch (_: InterruptedException) {
        }
        thread = null
        record = null
        segmenter.flush()?.let { onUtterance(Utterance(it, speechMs)) }
        onStateChange(false)
    }
}

/**
 * Sequential TTS chunk playback (SSE audio events). Chunks are decoded to temp
 * files and played one after another — MediaPlayer sniffs the container
 * (WAV/MP3), matching the desktop player's tolerance for both formats.
 * Reports playback state so the controller can run the turn-level echo gate.
 */
class TtsChunkPlayer(private val context: Context) {

    interface Listener {
        /** Any queued audio started playing (echo gate ON). */
        fun onPlaybackStarted()
        /** Queue drained and the last clip finished (echo gate OFF after tail). */
        fun onPlaybackEnded()
    }

    private val queue = ArrayDeque<String>()
    private var player: MediaPlayer? = null
    private var currentPath: String? = null
    private var listener: Listener? = null
    private var fileIndex = 0
    private val stopped = AtomicBoolean(true)

    fun setListener(value: Listener) {
        listener = value
    }

    val isPlaying: Boolean get() = !stopped.get() && player != null

    /** Enqueues one base64 audio chunk. Starts playback if idle. */
    @Synchronized
    fun enqueue(base64Audio: String) {
        val bytes = try {
            Base64.decode(base64Audio, Base64.DEFAULT)
        } catch (_: IllegalArgumentException) {
            return
        }
        if (bytes.isEmpty()) return
        val file = File(context.cacheDir, "tolik_tts_${fileIndex++}.audio")
        try {
            FileOutputStream(file).use { it.write(bytes) }
        } catch (_: Exception) {
            file.delete()
            return
        }
        queue.addLast(file.absolutePath)
        if (stopped.get()) playNextLocked()
    }

    @Synchronized
    private fun playNextLocked() {
        val path = queue.pollFirst() ?: run {
            stopped.set(true)
            listener?.onPlaybackEnded()
            return
        }
        stopped.set(false)
        currentPath = path
        listener?.onPlaybackStarted()
        val mediaPlayer = MediaPlayer()
        player = mediaPlayer
        try {
            mediaPlayer.setDataSource(path)
            // The temp file must live until the clip finishes — deletion
            // happens in the completion/error handlers, never eagerly.
            mediaPlayer.setOnCompletionListener {
                File(path).delete()
                it.release()
                player = null
                synchronized(this) { playNextLocked() }
            }
            mediaPlayer.setOnErrorListener { mp, _, _ ->
                File(path).delete()
                mp.release()
                player = null
                synchronized(this) { playNextLocked() }
                true
            }
            mediaPlayer.prepare()
            mediaPlayer.start()
        } catch (e: Exception) {
            File(path).delete()
            try {
                mediaPlayer.release()
            } catch (_: Exception) {
            }
            player = null
            playNextLocked()
        }
    }

    /** Barge-in: stop and release whatever is playing; drop the queue. */
    @Synchronized
    fun stopAll() {
        queue.clear()
        val current = player
        player = null
        current?.let { mp ->
            try {
                mp.setOnCompletionListener(null)
                mp.setOnErrorListener(null)
                mp.stop()
            } catch (_: Exception) {
            }
            try {
                mp.release()
            } catch (_: Exception) {
            }
        }
        currentPath?.let { File(it).delete() }
        currentPath = null
        val wasPlaying = !stopped.get()
        stopped.set(true)
        if (wasPlaying) listener?.onPlaybackEnded()
    }
}
