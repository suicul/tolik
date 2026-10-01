using System.CommandLine;
using TOLIK.Agent;

var coreUrlOption = new Option<string>("--core-url", "TOLIK Core base URL (e.g., http://127.0.0.1:5080)") { IsRequired = true };
var tokenOption = new Option<string>("--token", "Session token for authentication") { IsRequired = true };
var heartbeatOption = new Option<int>("--heartbeat-sec", "Heartbeat interval in seconds") { IsRequired = false };

var rootCommand = new RootCommand("TOLIK Windows Agent — execution body for TOLIK Core");
rootCommand.AddOption(coreUrlOption);
rootCommand.AddOption(tokenOption);
rootCommand.AddOption(heartbeatOption);

rootCommand.SetHandler(async (coreUrl, token, heartbeatSec) =>
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

    try
    {
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
        await agent.DisconnectAsync();
        Console.WriteLine("TOLIK.WindowsAgent stopped.");
    }
}, coreUrlOption, tokenOption, heartbeatOption);

return await rootCommand.InvokeAsync(args);