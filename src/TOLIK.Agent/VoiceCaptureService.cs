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
    private readonly UtteranceSegmenter _segmenter = new(preRollMs: 250, endSilenceMs: 700, maxUtteranceMs: 10_000);
    private WaveInEvent? _capture;
    private volatile bool _playing;

    public VoiceCaptureService(HttpClient http, string coreUrl, string voiceUrl, string token, VoiceActivationMode activation)
    {
        _http = http;
        _coreUrl = coreUrl.TrimEnd('/');
        _voiceUrl = voiceUrl.TrimEnd('/');
        _token = token;
        _activation = activation;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var (effective, fallbackReason) = VoiceActivation.Resolve(_activation, detector: null);
        if (fallbackReason is not null)
            Console.WriteLine($"[VOICE] activation: {effective} ({fallbackReason})");
        else
            Console.WriteLine($"[VOICE] activation: {effective}");

        using var gate = new SemaphoreSlim(1, 1);
        var chunkReady = new SemaphoreSlim(0, 1);
        byte[]? pending = null;

        _capture = new WaveInEvent
        {
            WaveFormat = new WaveFormat(16000, 16, 1),
            BufferMilliseconds = 480
        };
        _capture.DataAvailable += (_, e) =>
        {
            var data = new byte[e.BytesRecorded];
            Array.Copy(e.Buffer, data, data.Length);
            lock (this) { pending = data; }
            if (chunkReady.CurrentCount == 0) chunkReady.Release();
        };
        _capture.StartRecording();
        Console.WriteLine("[VOICE] mic open (16kHz mono, 480ms chunks). Say something.");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await chunkReady.WaitAsync(cancellationToken);
                byte[]? chunk;
                lock (this) { chunk = pending; pending = null; }
                if (chunk is null || chunk.Length == 0) continue;

                // Echo gate: while TOLIK's own reply plays, segmentation pauses —
                // otherwise the mic hears TOLIK and answers itself forever.
                if (_playing) continue;

                var hasSpeech = await DetectSpeechAsync(chunk, cancellationToken);
                var utterance = _segmenter.Feed(chunk, hasSpeech);
                if (_segmenter.IsCapturing)
                    Console.Write($"\r[VOICE] capturing... {_segmenter.IsCapturing}");
                if (utterance is null || utterance.Length == 0) continue;

                Console.WriteLine($"\r[VOICE] utterance complete ({utterance.Length / 32}ms) -> turn");
                await TurnAsync(utterance, cancellationToken);
            }
        }
        finally
        {
            StopRecording();
        }
    }

    private void StopRecording()
    {
        try { _capture?.StopRecording(); } catch { }
        _capture?.Dispose();
        _capture = null;
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
    /// print to the console, audio events play through NAudio in arrival order.</summary>
    private async Task TurnAsync(byte[] utterance, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_coreUrl}/voice/turn-stream");
        request.Headers.Authorization = new("Bearer", _token);
        request.Content = JsonContent.Create(new { audio = Convert.ToBase64String(ToWav(utterance)) });
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode == 401) { Console.WriteLine("[VOICE] turn: authentication required"); return; }
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[VOICE] turn failed: {(int)response.StatusCode}");
                return;
            }
            var isSse = response.Content.Headers.ContentType?.MediaType == "text/event-stream";
            if (!isSse) { Console.WriteLine("[VOICE] turn: intent/pause JSON reply (no SSE stream)"); return; }

            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken), Encoding.UTF8);
            var reply = new StringBuilder();
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
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
                    PlayAudio(Convert.FromBase64String(audio.GetString()!), cancellationToken);
                }
                else if (type == "done") { break; }
                else if (type == "error") { Console.WriteLine("[VOICE] model unavailable"); break; }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            Console.WriteLine("[VOICE] turn error: " + ex.Message);
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
