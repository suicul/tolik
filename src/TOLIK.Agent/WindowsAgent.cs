using System.Collections.Concurrent;
using System.Diagnostics;
using System.ComponentModel;
using System.Net.Http.Json;
using System.Text;
using System.Net.WebSockets;
using System.Threading.Channels;
using TOLIK.Contracts;

namespace TOLIK.Agent;

/// <summary>
/// Windows Agent — execution body. Connects to TOLIK Core and registers capabilities.
/// Agent is NOT an authorization authority. Core decides what is permitted.
/// </summary>
public sealed class WindowsAgent
{
    private readonly HttpClient _httpClient;
    private readonly string _agentId;
    private readonly string _registrationToken;
    private readonly string _coreUrl;
    private ClientWebSocket? _wsClient;
    private CancellationTokenSource? _wsCts;
    private Task? _wsReceiveTask;
    private readonly Channel<AgentCommandEnvelope> _wsCommandChannel = Channel.CreateBounded<AgentCommandEnvelope>(new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.DropOldest });
    private Timer? _heartbeatTimer;
    private readonly SemaphoreSlim _wsSendGate = new(1, 1);

    private async Task<bool> SendWsTextAsync(byte[] payload, CancellationToken cancellationToken)
    {
        var client = _wsClient;
        if (client is null || client.State != WebSocketState.Open) return false;
        await _wsSendGate.WaitAsync(cancellationToken);
        try
        {
            if (client.State != WebSocketState.Open) return false;
            await client.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        finally { _wsSendGate.Release(); }
    }

    private WindowsAgent(string coreUrl, string registrationToken)
    {
        _coreUrl = coreUrl;
        _registrationToken = registrationToken;
        var handler = new HttpClientHandler { 
            // Force HTTP/1.1 to avoid HTTP/2 multiplexing issues with long-running requests
            // that can block subsequent requests on the same connection
        };
        _httpClient = new HttpClient(handler) { BaseAddress = new Uri(coreUrl) };
        _httpClient.DefaultRequestVersion = new Version(1, 1);
        
        // Load or create persistent agent ID
        var idFile = Path.Combine(AppContext.BaseDirectory, "agent.id");
        if (File.Exists(idFile))
        {
            _agentId = File.ReadAllText(idFile).Trim();
        }
        else
        {
            _agentId = Guid.NewGuid().ToString("D");
            File.WriteAllText(idFile, _agentId);
        }
    }

    public bool IsOnline { get; private set; }
    public string AgentId => _agentId;

    public string StartupMessage() =>
        "TOLIK.WindowsAgent " + AgentId + " started. Enrollment=true; toolsEnabled=true; target=" + _coreUrl + ".";

    public static WindowsAgent Create(string coreUrl, string registrationToken)
    {
        return new WindowsAgent(coreUrl, registrationToken);
    }

    public async Task<AgentRegistration?> RegisterAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var msg = new HttpRequestMessage(HttpMethod.Post, "/agents/connect");
            msg.Headers.Authorization = new("Bearer", _registrationToken);
            msg.Content = new StringContent(BuildRegistrationJson(), Encoding.UTF8, "application/json");
            using var response = await _httpClient.SendAsync(msg, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                Console.Error.WriteLine("Agent registration failed: " + response.StatusCode + " — " + body);
                return null;
            }
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("agentId", out var agentIdProp))
            {
                Console.WriteLine("Agent registered successfully as " + agentIdProp.GetString());
            }
            IsOnline = true;
            LocalAgentRegistry.Register(CreateRegistration());
            // Connect WebSocket after successful registration
            await ConnectWebSocketAsync(cancellationToken);
            return CreateRegistration();
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine("Agent registration HTTP error: " + ex.Message);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private AgentRegistration CreateRegistration()
    {
        var caps = System.Collections.Immutable.ImmutableHashSet.Create(
            AgentCapability.ReadFile,
            AgentCapability.ListDirectory,
            AgentCapability.GetProcessStatus,
            AgentCapability.RunProcess,
            AgentCapability.RunBuild,
            AgentCapability.RunTests,
            AgentCapability.RunCodex,
            AgentCapability.RunOpenCode,
            AgentCapability.PlaySound);
        return new AgentRegistration(Guid.Parse(_agentId), caps);
    }

    public async Task<bool> HeartbeatAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var msg = new HttpRequestMessage(HttpMethod.Post, "/agents/heartbeat");
            msg.Headers.Authorization = new("Bearer", _registrationToken);
            msg.Content = new StringContent($"{{\"agentId\":\"{_agentId}\"}}", Encoding.UTF8, "application/json");
            using var response = await _httpClient.SendAsync(msg, cancellationToken);
            IsOnline = response.IsSuccessStatusCode;
            return IsOnline;
        }
        catch (HttpRequestException ex) { Console.WriteLine($"[AGENT HEARTBEAT] Error: {ex.Message}"); IsOnline = false; return false; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return false; }
    }

    public async Task<bool> DisconnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/agents/{_agentId}/disconnect");
            request.Headers.Authorization = new("Bearer", _registrationToken);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            IsOnline = false;
            LocalAgentRegistry.Remove(Guid.Parse(_agentId));
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { IsOnline = false; return false; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return false; }
    }

    /// <summary>Connects to the Core via WebSocket for command push (avoids HTTP polling head-of-line blocking).</summary>
    public async Task<bool> ConnectWebSocketAsync(CancellationToken cancellationToken = default)
    {
        if (_wsClient != null && _wsClient.State == WebSocketState.Open) return true;
        
        try
        {
            var wsUri = new Uri(_coreUrl.Replace("http://", "ws://").Replace("https://", "wss://") + "/agents/ws?token=" + Uri.EscapeDataString(_registrationToken) + "&agentId=" + Uri.EscapeDataString(_agentId));
            _wsClient = new ClientWebSocket();
            _wsClient.Options.SetRequestHeader("Authorization", "Bearer " + _registrationToken);
            _wsCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            
            await _wsClient.ConnectAsync(wsUri, _wsCts.Token);
            Console.WriteLine($"[AGENT WS] Connected to {_coreUrl}/agents/ws");
            
            // Start receive loop
            _wsReceiveTask = Task.Run(() => ReceiveWebSocketLoopAsync(_wsCts.Token), _wsCts.Token);
            
            // Start heartbeat timer (data message every 10s to keep connection alive).
            // Sends are serialized through _wsSendGate — ClientWebSocket allows
            // only one outstanding SendAsync, concurrent sends tear the socket down.
            _heartbeatTimer = new Timer(async _ =>
            {
                if (_wsClient?.State == WebSocketState.Open)
                {
                    try
                    {
                        var heartbeat = System.Text.Encoding.UTF8.GetBytes("{\"type\":\"heartbeat\"}");
                        await SendWsTextAsync(heartbeat, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine("[AGENT WS] Heartbeat send error: " + ex.Message);
                    }
                }
            }, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
            
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[AGENT WS] Connection failed: " + ex.Message);
            _wsClient = null;
            return false;
        }
    }

    private async Task ReceiveWebSocketLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        try
        {
            while (_wsClient?.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var result = await _wsClient.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var json = System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count);
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(json);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("type", out var typeProp))
                        {
                            var type = typeProp.GetString();
                            if (type == "hello")
                            {
                                Console.WriteLine($"[AGENT WS] Connected, user: {root.GetProperty("userId").GetString()}");
                            }
                            else if (type == "ping")
                            {
                                // Respond with pong to keep WebSocket alive (serialized send).
                                var pong = System.Text.Encoding.UTF8.GetBytes("{\"type\":\"pong\"}");
                                await SendWsTextAsync(pong, CancellationToken.None);
                            }
                            else if (type == "command")
                            {
                                var cmdId = Guid.Parse(root.GetProperty("commandId").GetString()!);
                                var capability = Enum.Parse<AgentCapability>(root.GetProperty("capability").GetString()!);
                                var argument = root.GetProperty("argument").GetString() ?? "";
                                var createdAt = DateTimeOffset.Parse(root.GetProperty("createdAt").GetString()!);
                                
                                var cmd = new AgentCommandEnvelope(cmdId, Guid.Parse(_agentId), capability, argument, createdAt);
                                Console.WriteLine($"[AGENT WS] Received command: {cmdId}, Capability: {capability}");
                                
                                // Queue for processing
                                _wsCommandChannel.Writer.TryWrite(cmd);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine("[AGENT WS] Parse error: " + ex.Message);
                    }
                }
            }
        }
        catch (OperationCanceledException) { Console.WriteLine("[AGENT WS] Receive loop cancelled"); }
        catch (WebSocketException ex)
        {
            Console.Error.WriteLine("[AGENT WS] WebSocket error: " + ex.Message);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[AGENT WS] Receive loop error: " + ex.Message);
        }
        finally
        {
            Console.WriteLine("[AGENT WS] Receive loop ended, reconnecting if online");
        }
    }

    public async Task<bool> SendCommandResponseAsync(AgentCommandResponse response, CancellationToken cancellationToken = default)
    {
        if (_wsClient == null || _wsClient.State != WebSocketState.Open) return false;

        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                type = "command_response",
                commandId = response.CommandId,
                outcome = (int)response.Outcome,
                result = response.Result,
                error = response.Error
            });
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            var sent = await SendWsTextAsync(bytes, cancellationToken);
            if (sent) Console.WriteLine($"[AGENT WS] Sent response for {response.CommandId}");
            return sent;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[AGENT WS] Send response error: " + ex.Message);
            return false;
        }
    }

    private async Task<bool> DisconnectWebSocketAsync(CancellationToken cancellationToken = default)
    {
        if (_wsClient != null)
        {
            try
            {
                if (_wsClient.State == WebSocketState.Open)
                {
                    await _wsClient.CloseAsync(WebSocketCloseStatus.NormalClosure, "agent_disconnect", cancellationToken);
                }
            }
            catch { }
            finally
            {
                _wsClient?.Dispose();
                _wsClient = null;
            }
            if (_wsCts != null)
            {
                _wsCts.Cancel();
                _wsCts.Dispose();
                _wsCts = null;
            }
            if (_wsReceiveTask != null)
            {
                try { await _wsReceiveTask; } catch { }
                _wsReceiveTask = null;
            }
            // Stop heartbeat timer
            if (_heartbeatTimer != null)
            {
                await _heartbeatTimer.DisposeAsync();
                _heartbeatTimer = null;
            }
        }
        return true;
    }

    public async Task<AgentCommandEnvelope?> PollCommandAsync(CancellationToken cancellationToken = default)
    {
        // First check WebSocket channel (instant, no HTTP overhead)
        if (_wsCommandChannel.Reader.TryRead(out var wsCmd))
        {
            Console.WriteLine($"[AGENT POLL] Got command from WS: {wsCmd.CommandId}, Capability: {wsCmd.Capability}");
            return wsCmd;
        }

        // Fall back to HTTP polling
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/agents/{_agentId}/commands/poll");
            request.Headers.Authorization = new("Bearer", _registrationToken);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await _httpClient.SendAsync(request, cts.Token);
            if (response.StatusCode == System.Net.HttpStatusCode.NoContent) return null;
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync(cts.Token);
            // Core sends enums as strings (JsonStringEnumConverter) — the agent must read them.
            var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
            {
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            };
            var cmd = System.Text.Json.JsonSerializer.Deserialize<AgentCommandEnvelope>(content, options);
            Console.WriteLine($"[AGENT POLL HTTP] Got command: {cmd?.CommandId}, Capability: {cmd?.Capability}");
            return cmd;
        }
        catch (HttpRequestException) { IsOnline = false; return null; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return null; }
        catch (TaskCanceledException) { Console.WriteLine($"[AGENT POLL HTTP] Timeout"); return null; }
        catch (Exception ex) { Console.WriteLine($"[AGENT POLL HTTP] Error: {ex.Message}"); return null; }
    }

    public async Task<bool> CompleteCommandAsync(AgentCommandResponse response, CancellationToken cancellationToken = default)
    {
        // Prefer HTTP for completion (more reliable than WebSocket for half-closed connections)
        try
        {
            Console.WriteLine($"[AGENT COMPLETE HTTP] Sending completion for {response.CommandId}, Outcome: {response.Outcome}");
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/agents/{_agentId}/commands/complete");
            request.Headers.Authorization = new("Bearer", _registrationToken);
            var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
            {
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            };
            request.Content = JsonContent.Create(response, options: options);
            using var result = await _httpClient.SendAsync(request, cancellationToken);
            result.EnsureSuccessStatusCode();
            Console.WriteLine($"[AGENT COMPLETE HTTP] Success for {response.CommandId}");
            return true;
        }
        catch (HttpRequestException) { IsOnline = false; return false; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return false; }
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _seenCommands = new();

    public async Task<bool> PollAndExecuteOnceAsync(CancellationToken cancellationToken = default)
    {
        var command = await PollCommandAsync(cancellationToken);
        if (command is null) return false;

        // Dedupe: same command may arrive via WS and HTTP polling — execute once.
        if (!_seenCommands.TryAdd(command.CommandId, 0))
        {
            Console.WriteLine($"[AGENT] Skipping duplicate command: {command.CommandId}");
            return true;
        }

        AgentCommandResponse response;
        try
        {
            var result = command.Capability switch
            {
                AgentCapability.ReadFile => await ReadAllowedFileAsync(command.Argument, cancellationToken),
                AgentCapability.ListDirectory => ListAllowedDirectory(command.Argument),
                AgentCapability.GetProcessStatus => GetProcessStatus(command.Argument, cancellationToken),
                AgentCapability.RunProcess => RunProcess(command.Argument, cancellationToken),
                AgentCapability.PlaySound => PlaySound(command.Argument, cancellationToken),
                AgentCapability.RunBuild => await RunDotnetAsync("build", command.Argument, cancellationToken),
                AgentCapability.RunTests => await RunDotnetAsync("test", command.Argument, cancellationToken),
                AgentCapability.RunCodex => await RunAssistantCliAsync("codex", command.Argument, cancellationToken),
                AgentCapability.RunOpenCode => await RunAssistantCliAsync("opencode", command.Argument, cancellationToken),
                _ => throw new UnauthorizedAccessException("Agent command capability is not implemented.")
            };
            response = new(command.CommandId, command.AgentId, CommandOutcome.Completed, result, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            response = new(command.CommandId, command.AgentId, CommandOutcome.Cancelled, null, "cancelled");
        }
        catch (UnauthorizedAccessException ex)
        {
            response = new(command.CommandId, command.AgentId, CommandOutcome.Failed, null, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or Win32Exception)
        {
            response = new(command.CommandId, command.AgentId, CommandOutcome.Failed, null, ex.Message);
        }

        return await CompleteCommandAsync(response, CancellationToken.None);
    }

    public async Task RunCommandLoopAsync(TimeSpan heartbeatInterval, CancellationToken cancellationToken = default)
    {
        if (heartbeatInterval <= TimeSpan.Zero || heartbeatInterval > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(heartbeatInterval));

        // Exponential backoff for reconnects: 5s -> 10s -> 20s -> 40s -> 80s (capped).
        var backoff = TimeSpan.FromSeconds(5);
        var maxBackoff = TimeSpan.FromSeconds(80);

        using var timer = new PeriodicTimer(heartbeatInterval);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!IsOnline)
                {
                    Console.WriteLine("Agent offline — attempting registration...");
                    var registration = await RegisterAsync(cancellationToken);
                    if (registration is not null)
                    {
                        backoff = TimeSpan.FromSeconds(5);
                        Console.WriteLine("Agent back online as " + registration.AgentId);
                        // Connect WebSocket after successful registration
                        await ConnectWebSocketAsync(cancellationToken);
                    }
                    else
                    {
                        Console.WriteLine("Registration failed — backing off " + backoff.TotalSeconds + "s.");
                        await Task.Delay(backoff, cancellationToken);
                        backoff = backoff >= maxBackoff ? maxBackoff : TimeSpan.FromSeconds(backoff.TotalSeconds * 2);
                    }
                }
                else
                {
                    // Ensure WebSocket is connected
                    if (_wsClient == null || _wsClient.State != WebSocketState.Open)
                    {
                        Console.WriteLine("[AGENT] WebSocket disconnected, reconnecting...");
                        await ConnectWebSocketAsync(cancellationToken);
                    }

                    await PollAndExecuteOnceAsync(cancellationToken);
                    if (IsOnline)
                    {
                        var alive = await HeartbeatAsync(cancellationToken);
                        if (!alive)
                        {
                            Console.WriteLine("Heartbeat lost — agent will re-register.");
                            IsOnline = false;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never let the loop die: log, mark offline, retry with backoff.
                Console.Error.WriteLine("Agent loop error: " + ex.Message);
                IsOnline = false;
                await Task.Delay(backoff, cancellationToken);
                backoff = backoff >= maxBackoff ? maxBackoff : TimeSpan.FromSeconds(backoff.TotalSeconds * 2);
            }

            try { await timer.WaitForNextTickAsync(cancellationToken); }
            catch (OperationCanceledException) { break; }
        }

        // Cleanup
        await DisconnectWebSocketAsync();
    }

    private static async Task<string> ReadAllowedFileAsync(string path, CancellationToken cancellationToken)
    {
        var fullPath = ResolveAllowedPath(path);
        var info = new FileInfo(fullPath);
        if (info.Length > 1_000_000) throw new InvalidOperationException("File size limit exceeded.");
        return await File.ReadAllTextAsync(fullPath, cancellationToken);
    }

    private static string ListAllowedDirectory(string path)
    {
        var fullPath = ResolveAllowedPath(path);
        return string.Join(Environment.NewLine, Directory.EnumerateFileSystemEntries(fullPath).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
    }

    private static string GetProcessStatus(string name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Any(char.IsWhiteSpace)) throw new ArgumentException("Invalid process name.");
        cancellationToken.ThrowIfCancellationRequested();
        var processes = Process.GetProcessesByName(name);
        try { return $"name={name}; count={processes.Length}; ids={string.Join(',', processes.Select(x => x.Id))}"; }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private static readonly Dictionary<string, string> AppNameToExecutable = new(StringComparer.OrdinalIgnoreCase)
    {
        { "калькулятор", "calc.exe" },
        { "calculator", "calc.exe" },
        { "блокнот", "notepad.exe" },
        { "notepad", "notepad.exe" },
        { "проводник", "explorer.exe" },
        { "explorer", "explorer.exe" },
        { "cmd", "cmd.exe" },
        { "консоль", "cmd.exe" },
        { "powershell", "powershell.exe" },
        { "pwsh", "pwsh.exe" },
    };

    private static string RunProcess(string spec, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(spec)) throw new ArgumentException("run_process requires spec: executable;args...");
        
        // Map common app names to executables
        if (AppNameToExecutable.TryGetValue(spec.Trim(), out var mappedExe))
        {
            spec = mappedExe;
        }
        
        var parts = spec.Split(';', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 1 || parts.Length > 4) throw new ArgumentException("run_process format: executable;arg1;arg2;...");
        var fileName = parts[0].Trim();
        if (fileName.Length == 0 || fileName.Length > 256) throw new ArgumentException("run_process executable is invalid.");

        // First attempt: console-style launch with captured output (works for CLI tools).
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (parts.Length > 1)
        {
            foreach (var arg in parts.Skip(1)) startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            // Fallback: Windows Store aliases (calc.exe, mspaint.exe on Win11) and GUI apps
            // cannot start with UseShellExecute=false; retry through the shell.
            var shellInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = true
            };
            if (parts.Length > 1) shellInfo.Arguments = string.Join(' ', parts.Skip(1));
            var shellProcess = Process.Start(shellInfo) ?? throw new InvalidOperationException("RunProcess failed: process not started");
            return $"exitCode=0;stdout=;stderr=;note=shell_gui_launch;pid={shellProcess.Id}";
        }

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            var exited = process.WaitForExit(30000);
            if (!exited)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return $"error=run_process_timeout;exitCode={process.ExitCode}";
            }
            return $"exitCode={process.ExitCode};stdout={stdout.Result};stderr={stderr.Result}";
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
    }

    private static async Task<string> RunDotnetAsync(string operation, string workingDirectory, CancellationToken cancellationToken)
    {
        var directory = ResolveAllowedPath(workingDirectory);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(operation);
        startInfo.ArgumentList.Add("--no-restore");
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("dotnet process could not start.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            return $"exitCode={process.ExitCode};stdout={await stdout};stderr={await stderr}";
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
    }

    private static Task<string> RunAssistantCliAsync(string executable, string argument, CancellationToken cancellationToken)
    {
        // Argument format: "workingDir|prompt" — user-controlled prompt (§9).
        // Fallback: legacy "workingDir" alone → safe repository inspection prompt.
        string directory;
        var prompt = "Inspect the current repository and report its status without modifying files.";
        var separator = argument.IndexOf('|');
        if (separator >= 0)
        {
            directory = argument[..separator].Trim();
            var userPrompt = argument[(separator + 1)..].Trim();
            if (userPrompt.Length > 0 && userPrompt.Length <= 4096)
            {
                // Prompt injection containment (§22): the CLI sees a scoped, one-shot task.
                // Destructive verbs are rejected — the assistant must not modify the repo via chat prompts.
                if (ContainsDestructiveDirective(userPrompt))
                    throw new UnauthorizedAccessException("Prompt contains a destructive directive; denied by the Agent allowlist.");
                prompt = userPrompt;
            }
        }
        else
        {
            directory = argument.Trim();
        }

        if (!Directory.Exists(ResolveAllowedPath(directory))) throw new DirectoryNotFoundException(directory);
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = ResolveAllowedPath(directory),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (executable.Equals("codex", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add("exec");
            startInfo.ArgumentList.Add("--ephemeral");
            startInfo.ArgumentList.Add("--skip-git-repo-check");
            startInfo.ArgumentList.Add("--color");
            startInfo.ArgumentList.Add("never");
            startInfo.ArgumentList.Add(prompt);
        }
        else
        {
            startInfo.ArgumentList.Add("run");
            startInfo.ArgumentList.Add(prompt);
        }
        return RunProcessAsync(startInfo, cancellationToken);
    }

    /// <summary>Destructive-verb screen for chat-originated CLI prompts (fail closed, §14).</summary>
    private static bool ContainsDestructiveDirective(string prompt)
    {
        var normalized = prompt.ToLowerInvariant();
        return normalized.Contains("rm -rf")
            || normalized.Contains("remove-item")
            || normalized.Contains("format ")
            || normalized.Contains("del /")
            || normalized.Contains("rd /s")
            || normalized.Contains("shutdown")
            || normalized.Contains("drop database")
            || normalized.Contains("drop table")
            || normalized.Contains("truncate table")
            || normalized.Contains("git push --force")
            || normalized.Contains("git reset --hard");
    }

    private static async Task<string> RunProcessAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Assistant process could not start.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            return $"exitCode={process.ExitCode};stdout={await stdout};stderr={await stderr}";
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
    }

    private static string ResolveAllowedPath(string path)
    {
        var roots = (Environment.GetEnvironmentVariable("TOLIK_ALLOWED_ROOTS") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return AgentPathGuard.ResolveAllowedPath(path, roots);
    }

    private string BuildRegistrationJson()
    {
        var caps = new[] { "ReadFile", "ListDirectory", "GetProcessStatus", "RunProcess", "RunBuild", "RunTests", "RunCodex", "RunOpenCode", "PlaySound" };
        var capsJson = string.Join(",", caps.Select(c => "\"" + c + "\""));
        return "{\"agentId\":\"" + _agentId + "\",\"advertisedCapabilities\":[" + capsJson + "]}";
    }

    /// <summary>
    /// Soundboard playback (AGENTS.md §17). Argument format: "category/name" or "category;name".
    /// The audio root is TOLIK_AUDIO_ROOT (default: %TOLIK_ROOT%\audio); only wav/mp3 allowed,
    /// category allowlist: reactions, memes, family, system. Non-blocking: fire-and-forget.
    /// </summary>
    private static string PlaySound(string spec, CancellationToken cancellationToken)
    {
        var parts = spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2) throw new ArgumentException("play_sound requires category;name");
        var category = parts[0].ToLowerInvariant();
        if (category is not ("reactions" or "memes" or "family" or "system"))
            throw new UnauthorizedAccessException("Category is not in the soundboard allowlist.");
        var name = parts[1];
        if (name.Contains("..", StringComparison.Ordinal) || name.Any(c => c is '\\' or '/'))
            throw new UnauthorizedAccessException("Sound name must be a plain file name.");

        var root = Environment.GetEnvironmentVariable("TOLIK_AUDIO_ROOT")
            ?? Path.Combine(AppContext.BaseDirectory, "audio");
        var file = Path.Combine(root, category, name);
        var resolved = Path.GetFullPath(file);
        var resolvedRoot = Path.GetFullPath(root);
        if (!resolved.StartsWith(resolvedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Sound path escaped the audio root.");
        if (!File.Exists(resolved)) throw new FileNotFoundException("Sound not found: " + category + "/" + name);
        var info = new FileInfo(resolved);
        if (info.Length > 20_000_000) throw new InvalidOperationException("Sound file exceeds the 20 MB limit.");

        _ = Task.Run(() =>
        {
            try
            {
                using var audio = new NAudio.Wave.AudioFileReader(resolved);
                using var output = new NAudio.Wave.WaveOutEvent();
                output.Init(audio);
                output.Play();
                while (output.PlaybackState == NAudio.Wave.PlaybackState.Playing)
                    Thread.Sleep(100);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("play_sound failed: " + ex.Message);
            }
        }, CancellationToken.None);

        return $"played={category}/{name}";
    }
}

/// <summary>
/// Simple in-memory registry for local agent tracking (MVP).
/// </summary>
public static class LocalAgentRegistry
{
    private static readonly ConcurrentDictionary<string, AgentRegistration> _agents = new();

    public static bool Register(AgentRegistration registration)
    {
        _agents[registration.AgentId.ToString()] = registration;
        return true;
    }
    public static bool Remove(Guid agentId) => _agents.TryRemove(agentId.ToString(), out _);
    public static bool Exists(string agentId) => _agents.ContainsKey(agentId);
    public static IReadOnlyList<AgentRegistration> GetAll() => _agents.Values.ToList();
    public static void Clear() => _agents.Clear();
}
