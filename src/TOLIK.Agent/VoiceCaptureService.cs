using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using NAudio.Wave;
using TOLIK.Contracts;

namespace TOLIK.Agent;

/// <summary>
/// Direct mic path (voice-runtime-v2.md 0.10): NAudio captures the mic; the voice
/// service's Silero VAD (POST /vad/detect, M6) plus UtteranceSegmenter shape the
/// utterances; each complete utterance becomes one /voice/turn-stream SSE request;
/// the audio events play through NAudio in arrival order. Echo gate: while TOLIK
/// speaks, segmentation pauses (the mic would hear TOLIK itself — honest v1; real
/// barge-in needs AEC, deferred). Activation (0.9): Always = VAD-gated; Wake without
/// a real detector falls back honestly; PushToTalk needs a hotkey (desktop client).
/// Core stays the authority — this only turns local audio into an authenticated turn.
/// </summary>
public sealed class VoiceCaptureService : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _coreUrl;
    private readonly string _voiceUrl;
    private readonly string _token;
    private readonly VoiceActivationMode _activation;
    private readonly int _deviceIndex;
    private readonly UtteranceSegmenter _segmenter = new(preRollMs: 250, endSilenceMs: 700, maxUtteranceMs: 10_000);
    private WaveInEvent? _capture;
    private volatile bool _playing;
    private CancellationTokenSource? _turnCts;

    /// <summary>How many sentences are spoken aloud per reply; the rest stay text
    /// (owner feedback live: "озвучивает все мысли" — voice replies must stay short).</summary>
    private const int MaxSpokenSentences = 3;

    public VoiceCaptureService(HttpClient http, string coreUrl, string voiceUrl, string token, VoiceActivationMode activation, int deviceIndex = 0)
    {
        _http = http;
        _coreUrl = coreUrl.TrimEnd('/');
        _voiceUrl = voiceUrl.TrimEnd('/');
        _token = token;
        _activation = activation;
        _deviceIndex = deviceIndex;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var (effective, fallbackReason) = VoiceActivation.Resolve(_activation, detector: null);
        if (fallbackReason is not null)
            Console.WriteLine($"[VOICE] activation: {effective} ({fallbackReason})");
        else
            Console.WriteLine($"[VOICE] activation: {effective}");

        Console.WriteLine("[VOICE] input devices (--voice-device <index>):");
        for (var i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            var caps = WaveInEvent.GetCapabilities(i);
            Console.WriteLine($"[VOICE]   #{i}: {caps.ProductName} (channels {caps.Channels})");
        }

        var chunkQueue = new System.Collections.Concurrent.ConcurrentQueue<byte[]>();
        var chunkReady = new SemaphoreSlim(0, 1);
        var accumulator = new MemoryStream(); // 16 kHz mono PCM between chunk boundaries
        var accumulatorLock = new object();

        Console.WriteLine("[VOICE] opening capture device...");
        var sourceFormat = ProbeFormat(_deviceIndex);
        _capture = new WaveInEvent { DeviceNumber = _deviceIndex, WaveFormat = sourceFormat, BufferMilliseconds = 480 };
        Console.WriteLine($"[VOICE] capture format: {sourceFormat}");
        _capture.DataAvailable += (_, e) =>
        {
            var mono = MixDownTo16kMono(e.Buffer, e.BytesRecorded, sourceFormat.SampleRate, sourceFormat.Channels);
            System.Collections.Generic.List<byte[]>? ready = null;
            lock (accumulatorLock)
            {
                accumulator.Write(mono, 0, mono.Length);
                const int chunkBytes = 15360; // 480ms @ 16 kHz mono 16-bit
                while (accumulator.Length >= chunkBytes)
                {
                    var chunk = new byte[chunkBytes];
                    var rest = new byte[accumulator.Length - chunkBytes];
                    accumulator.Position = 0;
                    accumulator.ReadExactly(chunk);
                    accumulator.ReadExactly(rest);
                    accumulator.SetLength(0);
                    accumulator.Write(rest, 0, rest.Length);
                    (ready ??= []).Add(chunk);
                }
            }
            if (ready is null) return;
            foreach (var chunk in ready)
            {
                chunkQueue.Enqueue(chunk);
                if (chunkReady.CurrentCount == 0) chunkReady.Release();
            }
        };
        _capture.StartRecording();
        Console.WriteLine($"[VOICE] mic open: device #{_deviceIndex} ({sourceFormat}) -> mixed to 16kHz mono, 480ms chunks. Say something.");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await chunkReady.WaitAsync(cancellationToken);
                while (chunkQueue.TryDequeue(out var chunk))
                {
                    // Console barge-in: ESC interrupts TOLIK — cancels the in-flight turn
                    // through Core (M5 /voice/cancel) and stops the playback. Voice
                    // barge-in while TOLIK speaks needs AEC — honest limitation, no fake.
                    // Hidden/redirected runs (no interactive console) simply have no ESC.
                    if (EscPressed())
                    {
                        Console.WriteLine("[VOICE] interrupt (ESC)");
                        await InterruptAsync(cancellationToken);
                        continue;
                    }

                    // Echo gate: while TOLIK's own reply plays, segmentation pauses —
                    // otherwise the mic hears TOLIK and answers itself forever.
                    if (_playing) continue;

                    var hasSpeech = await DetectSpeechAsync(chunk, cancellationToken);
                    var utterance = _segmenter.Feed(chunk, hasSpeech);
                    if (_segmenter.IsCapturing)
                        Console.Write("\r[VOICE] capturing...");
                    if (utterance is null || utterance.Length == 0) continue;

                    Console.WriteLine($"\r[VOICE] utterance complete ({utterance.Length / 32}ms) -> turn");
                    await TurnAsync(utterance, cancellationToken);
                }
            }
        }
        finally
        {
            StopRecording();
        }
    }

    /// <summary>
    /// Probes which format the device accepts: 48 kHz stereo first (USB interfaces'
    /// native), 44.1 kHz stereo fallback, 16 kHz mono last. The probe uses a THROWAWAY
    /// capture — StopRecording is asynchronous and leaves the instance unusable
    /// ("Already recording" on reuse, observed live) — the caller creates a fresh
    /// capture with the winning format. Two-input interfaces (e.g. Behringer
    /// UMC204HD) need STEREO capture with a mixdown: a mono capture hears only
    /// channel 1 — a mic on input 2 would record as silence.
    /// </summary>
    private static WaveFormat ProbeFormat(int deviceIndex)
    {
        foreach (var format in new[] { new WaveFormat(48000, 16, 2), new WaveFormat(44100, 16, 2), new WaveFormat(16000, 16, 1) })
        {
            using var probe = new WaveInEvent { DeviceNumber = deviceIndex, WaveFormat = format, BufferMilliseconds = 100 };
            try
            {
                probe.StartRecording();
                return format;
            }
            catch
            {
                // Try the next format.
            }
        }
        return new WaveFormat(16000, 16, 1);
    }

    /// <summary>Source capture -> 16 kHz mono 16-bit: L+R averaged (covers mics on
    /// either input of a 2-channel interface), nearest-neighbor decimation.</summary>
    private static byte[] MixDownTo16kMono(byte[] buffer, int bytesRecorded, int sourceRate, int sourceChannels)
    {
        const int targetRate = 16000;
        if (sourceRate == targetRate && sourceChannels == 1)
            return buffer[..bytesRecorded];
        var frames = bytesRecorded / (2 * sourceChannels);
        var outSamples = (int)((long)frames * targetRate / sourceRate);
        if (outSamples == 0) return [];
        var result = new byte[outSamples * 2];
        var step = (double)sourceRate / targetRate;
        var position = 0.0;
        for (var i = 0; i < outSamples; i++)
        {
            var frame = (int)position;
            var left = BitConverter.ToInt16(buffer, frame * 2 * sourceChannels);
            var right = sourceChannels >= 2
                ? BitConverter.ToInt16(buffer, frame * 2 * sourceChannels + 2)
                : left;
            BitConverter.GetBytes((short)((left + right) / 2)).CopyTo(result, i * 2);
            position += step;
        }
        return result;
    }

    /// <summary>ESC pressed on an INTERACTIVE console. Hidden/redirected runs (the
    /// detached agent, service mode) have no console keys — returns false instead of
    /// crashing (Console.KeyAvailable throws on redirected input, observed live).</summary>
    private static bool EscPressed()
    {
        try
        {
            return !Console.IsInputRedirected && Console.KeyAvailable && Console.ReadKey(intercept: true).Key == ConsoleKey.Escape;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void StopRecording()
    {
        try { _capture?.StopRecording(); } catch { }
        _capture?.Dispose();
        _capture = null;
    }

    /// <summary>ESC barge-in: cancels the in-flight turn through Core (M5 /voice/cancel
    /// — the registry knows the streaming turn) and drops any half-captured utterance.</summary>
    private async Task InterruptAsync(CancellationToken outer)
    {
        _turnCts?.Cancel();
        _segmenter.Flush();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_coreUrl}/voice/cancel");
            request.Headers.Authorization = new("Bearer", _token);
            using var response = await _http.SendAsync(request, outer);
        }
        catch { /* interrupt stays local even if Core is unreachable */ }
    }

    /// <summary>Silero VAD verdict for one chunk (voice service /vad/detect, M6).</summary>
    private async Task<bool> DetectSpeechAsync(byte[] wavChunk, CancellationToken cancellationToken)
    {
        try
        {
            using var form = new MultipartFormDataContent();
            var wav = ToWav(wavChunk);
            var file = new ByteArrayContent(wav);
            file.Headers.ContentType = new("audio/wav");
            form.Add(file, "file", "chunk.wav");
            using var response = await _http.PostAsync($"{_voiceUrl}/vad/detect", form, cancellationToken);
            if (!response.IsSuccessStatusCode) return false;
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            return doc.RootElement.TryGetProperty("hasSpeech", out var speech) && speech.GetBoolean();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false; // VAD unavailable -> treat as silence, never a fake speech
        }
    }

    /// <summary>One voice turn: the utterance WAV goes to /voice/turn-stream; text deltas
    /// print to the console, audio events play through NAudio in arrival order. The echo
    /// gate stays ON for the WHOLE turn (observed live: per-chunk gating leaks the TTS
    /// back into the mic between sentences — TOLIK answered itself in a loop) plus a
    /// decay tail; only the first MaxSpokenSentences play (the rest stay text — voice
    /// replies must be short, "все мысли вслух" was real feedback).</summary>
    private async Task TurnAsync(byte[] utterance, CancellationToken cancellationToken)
    {
        _turnCts?.Dispose();
        _turnCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var turnToken = _turnCts.Token;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_coreUrl}/voice/turn-stream");
        request.Headers.Authorization = new("Bearer", _token);
        request.Content = JsonContent.Create(new { audio = Convert.ToBase64String(ToWav(utterance)) });
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, turnToken);
            if ((int)response.StatusCode == 401) { Console.WriteLine("[VOICE] turn: authentication required"); return; }
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[VOICE] turn failed: {(int)response.StatusCode}");
                return;
            }
            var isSse = response.Content.Headers.ContentType?.MediaType == "text/event-stream";
            if (!isSse) { Console.WriteLine("[VOICE] turn: intent/pause JSON reply (no SSE stream)"); return; }

            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(turnToken), Encoding.UTF8);
            var reply = new StringBuilder();
            var spoken = 0;
            while (!turnToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(turnToken);
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data: ", StringComparison.Ordinal)) continue;
                using var doc = JsonDocument.Parse(line["data: ".Length..]);
                var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type == "delta" && doc.RootElement.TryGetProperty("text", out var text))
                    reply.Append(text.GetString());
                else if (type == "audio" && doc.RootElement.TryGetProperty("audio", out var audio))
                {
                    Console.WriteLine($"[VOICE] TOLIK: {reply}");
                    reply.Clear();
                    spoken++;
                    if (spoken > MaxSpokenSentences)
                    {
                        Console.WriteLine("[VOICE] (further reply is text-only: voice replies stay short)");
                        continue;
                    }
                    // The whole-turn echo gate: ON from the FIRST spoken sentence.
                    _playing = true;
                    PlayAudio(Convert.FromBase64String(audio.GetString()!), turnToken);
                }
                else if (type == "done") { break; }
                else if (type == "error") { Console.WriteLine("[VOICE] model unavailable"); break; }
            }
        }
        catch (OperationCanceledException) when (turnToken.IsCancellationRequested)
        {
            Console.WriteLine("[VOICE] turn interrupted");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            Console.WriteLine("[VOICE] turn error: " + ex.Message);
        }
        finally
        {
            if (_playing)
            {
                // Decay tail: the room reverb must settle before the mic reopens,
                // otherwise the tail becomes a spurious utterance (observed live).
                try { await Task.Delay(400, cancellationToken); } catch (OperationCanceledException) { }
                _playing = false;
            }
            _turnCts?.Dispose();
            _turnCts = null;
        }
    }

    /// <summary>Sequential playback (echo gate on); WAV and MP3 blobs both supported.</summary>
    private void PlayAudio(byte[] audio, CancellationToken cancellationToken)
    {
        _playing = true;
        try
        {
            using var stream = new MemoryStream(audio);
            using var reader = IsWav(audio)
                ? new WaveFileReader(stream)
                : (WaveStream)new Mp3FileReader(stream);
            using var output = new WaveOutEvent();
            output.Init(reader);
            output.Play();
            while (output.PlaybackState == PlaybackState.Playing && !cancellationToken.IsCancellationRequested)
                Thread.Sleep(50);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[VOICE] playback error: " + ex.Message);
        }
        finally
        {
            _playing = false;
        }
    }

    private static bool IsWav(byte[] data) =>
        data.Length > 12 && data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F';

    /// <summary>16 kHz mono 16-bit PCM -> WAV container (the voice service/core format).</summary>
    private static byte[] ToWav(byte[] pcm)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write("RIFF"u8); bw.Write(36 + pcm.Length); bw.Write("WAVE"u8);
        bw.Write("fmt "u8); bw.Write(16); bw.Write((short)1); bw.Write((short)1);
        bw.Write(16000); bw.Write(32000); bw.Write((short)2); bw.Write((short)16);
        bw.Write("data"u8); bw.Write(pcm.Length); bw.Write(pcm);
        bw.Flush();
        return ms.ToArray();
    }

    public void Dispose() => StopRecording();
}
