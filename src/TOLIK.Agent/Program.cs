using System.CommandLine;
using TOLIK.Agent;
using TOLIK.Contracts;

var coreUrlOption = new Option<string>("--core-url", "TOLIK Core base URL (e.g., http://127.0.0.1:5080)") { IsRequired = true };
var tokenOption = new Option<string>("--token", "Session token for authentication") { IsRequired = true };
var heartbeatOption = new Option<int>("--heartbeat-sec", "Heartbeat interval in seconds") { IsRequired = false };
var voiceOption = new Option<bool>("--voice", "Direct mic path (voice-runtime-v2.md 0.10): capture -> VAD -> turn -> playback") { IsRequired = false };
var voiceServiceOption = new Option<string>("--voice-service-url", () => "http://127.0.0.1:9660", "Voice service base URL (VAD /vad/detect)") { IsRequired = false };
var voiceActivationOption = new Option<string>("--voice-activation", () => "always", "Activation mode (0.9): always | wake | ptt — no fake: wake without a real detector falls back to always") { IsRequired = false };

var rootCommand = new RootCommand("TOLIK Windows Agent — execution body for TOLIK Core");
rootCommand.AddOption(coreUrlOption);
rootCommand.AddOption(tokenOption);
rootCommand.AddOption(heartbeatOption);
rootCommand.AddOption(voiceOption);
rootCommand.AddOption(voiceServiceOption);
rootCommand.AddOption(voiceActivationOption);

rootCommand.SetHandler(async (coreUrl, token, heartbeatSec, voice, voiceServiceUrl, voiceActivation) =>
{
    var heartbeatInterval = TimeSpan.FromSeconds(heartbeatSec > 0 ? heartbeatSec : 30);
    Console.WriteLine("TOLIK.WindowsAgent starting...");
    Console.WriteLine("Core URL: " + coreUrl);
    Console.WriteLine("Heartbeat: " + heartbeatInterval.TotalSeconds + "s");

    var agent = WindowsAgent.Create(coreUrl, token);
    Console.WriteLine(agent.StartupMessage());

    var cancellationSource = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        Console.WriteLine("Shutdown signal received...");
        cancellationSource.Cancel();
    };

    VoiceCaptureService? voiceCapture = null;
    try
    {
        if (voice)
        {
            // Direct mic path (0.10) alongside the command loop. The activation mode
            // (0.9) resolves honestly inside (wake without a detector -> always).
            voiceCapture = new VoiceCaptureService(
                new HttpClient(), coreUrl, voiceServiceUrl, token,
                VoiceActivation.Parse(voiceActivation));
            _ = voiceCapture.RunAsync(cancellationSource.Token);
        }

        await agent.RunCommandLoopAsync(heartbeatInterval, cancellationSource.Token);
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("Agent command loop cancelled.");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("Agent fatal error: " + ex.Message);
        Environment.ExitCode = 1;
    }
    finally
    {
        voiceCapture?.Dispose();
        await agent.DisconnectAsync();
        Console.WriteLine("TOLIK.WindowsAgent stopped.");
    }
}, coreUrlOption, tokenOption, heartbeatOption, voiceOption, voiceServiceOption, voiceActivationOption);

return await rootCommand.InvokeAsync(args);
