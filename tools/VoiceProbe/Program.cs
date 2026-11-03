// Live check of the desktop voice path against the running TOLIK stack.
// Not a unit test: it needs Core, the voice service and a real session token.
// Usage: dotnet run --project tools/VoiceProbe <path-to-wav>
//
// Authentication goes through ClientTokenSource, i.e. the shipped client's path: the
// device signs a challenge and Core issues a session. It used to build a LocalTokenSource
// (dev token only), which stops working the moment Genesis has run — so the probe was
// silently unusable on any real installation.
using TOLIK.Desktop.Bridge;
using TOLIK.Desktop.Voice;

Console.OutputEncoding = System.Text.Encoding.UTF8;

CoreLinkOptions options = CoreLinkOptions.FromEnvironment();
Console.WriteLine($"core: {options.BaseUri}");

using ClientTokenSource tokens = ClientTokenSource.Create(options);
Console.WriteLine($"auth: device={tokens.HasDeviceIdentity}");
TokenProbe probe = new(tokens, options);
string wavPath = args.Length > 0 ? args[0] : string.Empty;
if (string.IsNullOrWhiteSpace(wavPath) || !File.Exists(wavPath))
{
    Console.WriteLine("usage: VoiceProbe <path-to-wav>");
    return 2;
}

byte[] wav = File.ReadAllBytes(wavPath);
Console.WriteLine($"audio: {wavPath} ({wav.Length} bytes, 16 kHz mono expected)");

using var voice = new VoiceTurnClient(options, tokens);
int audioChunks = 0;
int audioBytes = 0;
var deltas = new List<string>();

var sw = System.Diagnostics.Stopwatch.StartNew();
long firstDelta = 0, firstAudio = 0;
VoiceTurnResult result = await voice.SendAsync(wav, evt =>
{
    switch (evt)
    {
        case VoiceStreamEvent.Delta delta:
            if (firstDelta == 0) firstDelta = sw.ElapsedMilliseconds;
            deltas.Add(delta.Text);
            break;
        case VoiceStreamEvent.Audio audio:
            if (firstAudio == 0) firstAudio = sw.ElapsedMilliseconds;
            audioChunks++;
            audioBytes += audio.Data.Length;
            break;
    }
}, CancellationToken.None);

Console.WriteLine($"status: {result.Status}");
Console.WriteLine($"detail: {result.Detail ?? "-"}");
Console.WriteLine($"transcript: {result.Transcript}");
Console.WriteLine($"deltas: {deltas.Count}, audio chunks: {audioChunks} ({audioBytes} bytes)");
Console.WriteLine($"first delta: {firstDelta} ms, first audio: {firstAudio} ms, total: {sw.ElapsedMilliseconds} ms");
return result.IsSuccess ? 0 : 1;

internal sealed class TokenProbe(ISessionTokenSource tokens, CoreLinkOptions options)
{
    public override string ToString() => $"{options.BaseUri} / {tokens.LastDetail}";
}
