using System.CommandLine;
using TOLIK.Agent;
using TOLIK.Contracts;

// UTF-8 console: Cyrillic replies and device names must not turn into mojibake
// (observed live in redirected logs).
Console.OutputEncoding = System.Text.Encoding.UTF8;

var coreUrlOption = new Option<string>("--core-url", "TOLIK Core base URL (e.g., http://127.0.0.1:5080)") { IsRequired = true };
var tokenOption = new Option<string>("--token", "Session token (or TOLIK_SESSION_TOKEN env — preferred: command lines are readable by any local process, threats T3/T8)") { IsRequired = false };
var heartbeatOption = new Option<int>("--heartbeat-sec", "Heartbeat interval in seconds") { IsRequired = false };
var voiceOption = new Option<bool>("--voice", "Direct mic path (voice-runtime-v2.md 0.10): capture -> VAD -> turn -> playback") { IsRequired = false };
var voiceServiceOption = new Option<string>("--voice-service-url", () => "http://127.0.0.1:9660", "Voice service base URL (VAD /vad/detect)") { IsRequired = false };
var voiceActivationOption = new Option<string>("--voice-activation", () => "always", "Activation mode (0.9): always | wake | ptt — no fake: wake without a real detector falls back to always") { IsRequired = false };
var voiceDeviceOption = new Option<int>("--voice-device", () => 0, "WaveIn device index for the mic (printed at --voice startup)") { IsRequired = false };

var rootCommand = new RootCommand("TOLIK Windows Agent — execution body for TOLIK Core");
rootCommand.AddOption(coreUrlOption);
rootCommand.AddOption(tokenOption);
rootCommand.AddOption(heartbeatOption);
rootCommand.AddOption(voiceOption);
rootCommand.AddOption(voiceServiceOption);
rootCommand.AddOption(voiceActivationOption);
rootCommand.AddOption(voiceDeviceOption);

rootCommand.SetHandler(async (coreUrl, token, heartbeatSec, voice, voiceServiceUrl, voiceActivation, voiceDevice) =>
{
    var heartbeatInterval = TimeSpan.FromSeconds(heartbeatSec > 0 ? heartbeatSec : 30);
    Console.WriteLine("TOLIK.WindowsAgent starting...");
    Console.WriteLine("Core URL: " + coreUrl);
    Console.WriteLine("Heartbeat: " + heartbeatInterval.TotalSeconds + "s");

    // Env-token fallback (audit m6): prefer the environment over the command line —
    // command lines are readable by any local process via Win32_Process (threats T3/T8).
    // Fail closed: the agent never starts without a token (never an anonymous agent).
    token ??= Environment.GetEnvironmentVariable("TOLIK_SESSION_TOKEN");
    if (string.IsNullOrWhiteSpace(token))
    {
        Console.Error.WriteLine("No session token: pass --token or set TOLIK_SESSION_TOKEN (env preferred).");
        Environment.ExitCode = 1;
        return;
    }

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
                VoiceActivation.Parse(voiceActivation), voiceDevice);
            _ = Task.Run(async () =>
            {
                try
                {
                    await voiceCapture.RunAsync(cancellationSource.Token);
                }
                catch (OperationCanceledException) when (cancellationSource.Token.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    // The voice loop must never die silently — a swallowed task
                    // exception is how the mic "mysteriously stops" (observed live).
                    Console.Error.WriteLine("[VOICE] loop crashed: " + ex);
                }
            });
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
}, coreUrlOption, tokenOption, heartbeatOption, voiceOption, voiceServiceOption, voiceActivationOption, voiceDeviceOption);

return await rootCommand.InvokeAsync(args);
