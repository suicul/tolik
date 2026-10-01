using NAudio.Wave;

namespace TOLIK.Cli;

/// <summary>
/// Windows microphone capture + WAV playback for the TOLIK voice loop.
/// Records until Enter is pressed, then emits a 16kHz mono 16-bit WAV payload
/// (the format Whisper expects) ready for /voice/turn.
/// </summary>
public static class VoiceIO
{
    public static byte[] RecordUntilEnter(int sampleRate = 16000)
    {
        if (!IsWindows())
            throw new PlatformNotSupportedException("Microphone capture is currently Windows-only (NAudio).");

        using var capture = new WaveInEvent { WaveFormat = new WaveFormat(sampleRate, 16, 1), BufferMilliseconds = 100 };
        using var buffer = new MemoryStream();
        var writer = new WaveFileWriter(buffer, capture.WaveFormat);
        var gate = new ManualResetEventSlim(false);

        capture.DataAvailable += (_, e) => writer.Write(e.Buffer, 0, e.BytesRecorded);
        capture.RecordingStopped += (_, _) => gate.Set();

        Console.Write("Recording... press ENTER to stop. ");
        capture.StartRecording();
        Console.ReadLine();
        capture.StopRecording();
        gate.Wait(TimeSpan.FromSeconds(2));
        writer.Dispose(); // flushes WAV header
        return buffer.ToArray();
    }

    public static void PlayWav(byte[] wav)
    {
        if (wav is null || wav.Length == 0) return;
        // MediaFoundationReader needs a file; temp file handles both WAV (SAPI) and MP3 (edge-tts).
        var tempPath = Path.Combine(Path.GetTempPath(), "tolik-reply-" + Guid.NewGuid().ToString("N")[..8] + AudioExtensionFor(wav));
        try
        {
            File.WriteAllBytes(tempPath, wav);
            using var reader = new MediaFoundationReader(tempPath);
            using var output = new WaveOutEvent();
            output.Init(reader);
            output.Play();
            while (output.PlaybackState == PlaybackState.Playing)
                Thread.Sleep(100);
        }
        finally
        {
            try { File.Delete(tempPath); } catch (IOException) { }
        }
    }

    private static string AudioExtensionFor(byte[] audio)
    {
        // MP3 (edge-tts): ID3 tag or MPEG frame sync. Everything else: treat as WAV.
        if (audio.Length > 3 && audio[0] == 0x49 && audio[1] == 0x44 && audio[2] == 0x33) return ".mp3";
        if (audio.Length > 1 && audio[0] == 0xFF && (audio[1] & 0xE0) == 0xE0) return ".mp3";
        return ".wav";
    }

    public static byte[] ReadWavFile(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException(path);
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > 20_971_520) throw new ArgumentException("Audio file exceeds the 20 MB limit.");
        return bytes;
    }

    private static bool IsWindows() => OperatingSystem.IsWindows();
}
