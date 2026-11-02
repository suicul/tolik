using NAudio.Wave;

// The desktop project does not enable implicit usings; System.IO is needed for
// MemoryStream and exception types used below.
using System.IO;

namespace TOLIK.Desktop.Voice;

/// <summary>
/// The PC microphone. Records 16 kHz mono 16-bit PCM — the format Core's STT expects —
/// and plays TOLIK's speech through the default output device.
///
/// Deliberately a thin device wrapper: no VAD, no endpointing policy, no barge-in
/// decisions. The user presses to talk and presses to stop; Core decides what the
/// audio means. Automatic endpointing belongs upstream (the Agent already does
/// VAD-gated capture), and putting a second, weaker VAD here would only add a way to
/// lose the beginning of a sentence.
///
/// One capture at a time, guarded: a stuck capture that cannot be stopped is the worst
/// failure mode for a microphone (the indicator would lie and the device would stay
/// busy), so <see cref="IsRecording"/> reflects the real device state.
/// </summary>
public sealed class MicrophoneRecorder : IDisposable
{
    private readonly List<byte> _buffer = [];
    private readonly object _gate = new();
    private WaveInEvent? _capture;
    private WaveOutEvent? _output;
    private bool _disposed;

    /// <summary>Raised with the current peak level (0..1) so the UI can show input.</summary>
    public event Action<double>? LevelChanged;

    public bool IsRecording
    {
        get { lock (_gate) { return _capture is not null; } }
    }

    /// <summary>Opens the default input device. Returns false with an honest reason.</summary>
    public bool Start(out string? error)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                error = "клиент закрывается";
                return false;
            }
            if (_capture is not null)
            {
                error = null;
                return true;
            }

            try
            {
                if (WaveInEvent.DeviceCount == 0)
                {
                    error = "микрофон не найден";
                    return false;
                }

                _buffer.Clear();
                var capture = new WaveInEvent
                {
                    DeviceNumber = 0,
                    WaveFormat = new WaveFormat(AudioPcm.TargetSampleRate, AudioPcm.TargetBitsPerSample, AudioPcm.TargetChannels),
                    BufferMilliseconds = 100,
                };
                capture.DataAvailable += OnDataAvailable;
                capture.RecordingStopped += OnRecordingStopped;
                capture.StartRecording();
                _capture = capture;
                error = null;
                return true;
            }
            catch (Exception exception)
            {
                // Device busy / driver refusing the format — a real condition, not a bug.
                error = $"не удалось открыть микрофон ({exception.GetType().Name})";
                StopCore();
                return false;
            }
        }
    }

    /// <summary>
    /// Stops the device and returns the recording as a WAV container. Empty array when
    /// nothing was captured — the caller reports that, never a fabricated turn.
    /// </summary>
    public byte[] Stop()
    {
        byte[] pcm;
        lock (_gate)
        {
            StopCore();
            pcm = _buffer.ToArray();
            _buffer.Clear();
        }
        return pcm.Length == 0 ? [] : AudioPcm.ToWav(pcm);
    }

    /// <summary>Plays one WAV chunk from Core. Plays are serialized so sentence chunks
    /// never overlap and garble the speech.</summary>
    public void Play(byte[] wav)
    {
        if (wav is null || wav.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                StopPlaybackCore();
                var stream = new MemoryStream(wav, writable: false);
                var reader = new WaveFileReader(stream);
                var output = new WaveOutEvent();
                output.PlaybackStopped += (_, _) =>
                {
                    try { output.Dispose(); } catch { /* device gone */ }
                    try { reader.Dispose(); } catch { /* already closed */ }
                    try { stream.Dispose(); } catch { /* already closed */ }
                };
                output.Init(reader);
                output.Play();
                _output = output;
            }
            catch (Exception)
            {
                // A broken output device must never break the turn: the answer is still
                // rendered as text, so the user loses the audio, not the response.
                StopPlaybackCore();
            }
        }
    }

    /// <summary>Stops speech — used for barge-in when the user starts talking again.</summary>
    public void StopPlayback()
    {
        lock (_gate)
        {
            StopPlaybackCore();
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0)
        {
            return;
        }

        double level;
        lock (_gate)
        {
            if (_capture is null)
            {
                return;
            }
            _buffer.AddRange(new ReadOnlySpan<byte>(e.Buffer, 0, e.BytesRecorded).ToArray());
            level = AudioPcm.PeakLevel(new ReadOnlySpan<byte>(e.Buffer, 0, e.BytesRecorded).ToArray());
        }
        LevelChanged?.Invoke(level);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        // The device can stop on its own (unplugged). Reflect reality: a capture we
        // cannot use must not keep reporting IsRecording.
        lock (_gate)
        {
            if (_capture is not null)
            {
                StopCore();
            }
        }
        if (e.Exception is not null)
        {
            LevelChanged?.Invoke(0.0);
        }
    }

    private void StopCore()
    {
        if (_capture is null)
        {
            return;
        }

        WaveInEvent capture = _capture;
        _capture = null;
        try { capture.DataAvailable -= OnDataAvailable; } catch { /* detached */ }
        try { capture.RecordingStopped -= OnRecordingStopped; } catch { /* detached */ }
        try { capture.StopRecording(); } catch { /* already stopped */ }
        try { capture.Dispose(); } catch { /* driver gone */ }
    }

    private void StopPlaybackCore()
    {
        if (_output is null)
        {
            return;
        }

        WaveOutEvent output = _output;
        _output = null;
        try { output.Stop(); } catch { /* already stopped */ }
        try { output.Dispose(); } catch { /* driver gone */ }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            StopCore();
            StopPlaybackCore();
            _buffer.Clear();
        }
    }
}
