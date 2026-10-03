using System.Text.Json;
using TOLIK.Cli;

if (args.Any(arg => arg is "--help" or "-h" or "help"))
{
    PrintUsage();
    return 0;
}

var endpoint = GetOption(args, "--endpoint") ?? "http://127.0.0.1:5080";
var token = GetOption(args, "--token") ?? Environment.GetEnvironmentVariable("TOLIK_SESSION_TOKEN");
var command = args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal))?.ToLowerInvariant() ?? "status";
if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri) || endpointUri.Scheme is not ("http" or "https"))
{
    Console.Error.WriteLine("Endpoint must be an absolute http or https URL.");
    return 64;
}

using var httpClient = new HttpClient();
var client = new CoreApiClient(httpClient, endpointUri, token);
try
{
    switch (command)
    {
        case "status": return await PrintAsync(await client.GetStatusAsync());
        case "modules": return await PrintAsync(await client.GetModulesAsync());
        case "agents": return await PrintAsync(await client.GetAgentsAsync());
        case "chat": return await PrintAsync(await client.SendChatAsync(GetRemaining(args, command)));
        case "task": return await RunTaskAsync(client, args);
        case "memory": return await RunMemoryAsync(client, args);
        case "bootstrap": return await RunBootstrapAsync(httpClient, endpointUri, args);
        case "transcribe": return await RunTranscribeAsync(client, args);
        case "say": return await RunSayAsync(client, args);
        case "listen": return await RunListenAsync(client, args);
        case "remind": return await RunRemindAsync(client, args);
        case "sound": return await RunSoundAsync(client, args);
        default:
            PrintUsage();
            return 64;
    }
}
catch (HttpRequestException exception)
{
    Console.Error.WriteLine($"Core API unavailable: {exception.Message}");
    return 1;
}

static async Task<int> RunTaskAsync(CoreApiClient client, string[] args)
{
    var action = args.FirstOrDefault(arg => arg is "create" or "get" or "cancel");
    if (action is null)
    {
        Console.Error.WriteLine("Usage: tolik task create --description TEXT | get TASK_ID | cancel TASK_ID");
        return 64;
    }
    if (action == "create")
    {
        var description = GetOption(args, "--description");
        return description is null ? 64 : await PrintAsync(await client.CreateTaskAsync(description));
    }
    var idText = args.SkipWhile(arg => !string.Equals(arg, action, StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault();
    if (!Guid.TryParse(idText, out var taskId))
    {
        Console.Error.WriteLine("Task ID must be a GUID.");
        return 64;
    }
    if (action == "get") return await PrintAsync(await client.GetTaskAsync(taskId));
    if (action == "cancel") return await PrintAsync(await client.CancelTaskAsync(taskId));
    throw new InvalidOperationException("Unsupported task action.");
}

static async Task<int> PrintAsync<T>(ApiResponse<T> result)
{
    Console.WriteLine(JsonSerializer.Serialize(new { status = (int)result.StatusCode, data = result.Value, error = result.Error }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    await Task.CompletedTask;
    return result.IsSuccess ? 0 : result.IsAuthorizationFailure ? 2 : 1;
}

static string? GetOption(string[] arguments, string name)
{
    var index = Array.FindIndex(arguments, argument => string.Equals(argument, name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
}

static string GetRemaining(string[] arguments, string command)
{
    var index = Array.FindIndex(arguments, argument => string.Equals(argument, command, StringComparison.OrdinalIgnoreCase));
    if (index < 0) return string.Empty;
    var message = new List<string>();
    for (var i = index + 1; i < arguments.Length; i++)
    {
        if (arguments[i].StartsWith("--", StringComparison.Ordinal))
        {
            i++; // skip the option's value so it never leaks into the message
            continue;
        }
        message.Add(arguments[i]);
    }
    return string.Join(' ', message);
}

static async Task<int> RunMemoryAsync(CoreApiClient client, string[] args)
{
    var query = GetOption(args, "--query");
    if (query is null)
    {
        Console.Error.WriteLine("Usage: tolik memory --query TEXT");
        return 64;
    }
    return await PrintAsync(await client.SearchMemoryAsync(query));
}

static async Task<int> RunTranscribeAsync(CoreApiClient client, string[] args)
{
    var path = GetOption(args, "--file");
    if (path is null || !File.Exists(path))
    {
        Console.Error.WriteLine("Usage: tolik transcribe --file PATH");
        return 64;
    }
    var audio = await File.ReadAllBytesAsync(path);
    return await PrintAsync(await client.TranscribeFileAsync(audio));
}

static async Task<int> RunSayAsync(CoreApiClient client, string[] args)
{
    var path = GetOption(args, "--file");
    if (path is null || !File.Exists(path))
    {
        Console.Error.WriteLine("Usage: tolik say --file PATH");
        return 64;
    }
    var audio = await File.ReadAllBytesAsync(path);
    var result = await client.VoiceTurnAsync(audio);
    if (result.IsSuccess && result.Value is not null)
    {
        Console.WriteLine("TOLIK: " + result.Value.ReplyText);
        if (result.Value.Audio is { Length: > 0 })
        {
            var output = GetOption(args, "--out") ?? Path.ChangeExtension(path, ".reply.wav");
            await File.WriteAllBytesAsync(output, result.Value.Audio);
            Console.WriteLine("Audio reply saved to: " + output);
        }
        return 0;
    }
    return await PrintAsync(result);
}

static async Task<int> RunBootstrapAsync(HttpClient httpClient, Uri endpoint, string[] args)
{
    var bootstrapCode = GetOption(args, "--code") ?? Environment.GetEnvironmentVariable("TOLIK_BOOTSTRAP_CODE");
    if (string.IsNullOrWhiteSpace(bootstrapCode))
    {
        Console.Error.WriteLine("Bootstrap code required: --code TLK-XXXX or TOLIK_BOOTSTRAP_CODE env var.");
        return 64;
    }
    var machineId = Environment.MachineName + "-" + Environment.UserName;

    // Installation key is stored in DPAPI-protected file — copying the folder must NOT clone identity (AGENTS.md).
    var keyDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tolik");
    Directory.CreateDirectory(keyDir);
    var keyPath = Path.Combine(keyDir, "device-key.xml");
    var installationPath = Path.Combine(keyDir, "installation-id");

    System.Security.Cryptography.ECDsa key;
    if (File.Exists(keyPath))
    {
        key = System.Security.Cryptography.ECDsa.Create();
        key.ImportECPrivateKey(Convert.FromBase64String(File.ReadAllText(keyPath).Trim()), out _);
        Console.WriteLine("Reusing existing device key.");
    }
    else
    {
        key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        File.WriteAllText(keyPath, Convert.ToBase64String(key.ExportECPrivateKey()));
        Console.WriteLine("Generated new P-256 device key at " + keyPath);
    }

    var installationId = File.Exists(installationPath)
        ? (await File.ReadAllTextAsync(installationPath)).Trim()
        : Guid.NewGuid().ToString("N");
    await File.WriteAllTextAsync(installationPath, installationId);

    var bootstrapClient = new CoreApiClient(httpClient, endpoint, null);
    var enrolled = await bootstrapClient.BootstrapEnrollAsync(bootstrapCode, installationId, key);
    if (enrolled is null || !enrolled.IsSuccess || enrolled.Value?.SessionToken is null)
    {
        Console.Error.WriteLine("Enrollment failed: " + (enrolled?.Error ?? enrolled?.StatusCode.ToString() ?? "unknown"));
        return 2;
    }

    var tokenPath = Path.Combine(keyDir, "session-token");
    await File.WriteAllTextAsync(tokenPath, enrolled.Value.SessionToken);
    Console.WriteLine("Enrolled OK.");
    Console.WriteLine("  userId:    " + enrolled.Value.UserId);
    Console.WriteLine("  deviceId:  " + enrolled.Value.DeviceId);
    Console.WriteLine("  sessionId: " + enrolled.Value.SessionId);
    Console.WriteLine("Token saved to " + tokenPath);
    Console.WriteLine("Use it with: --token $(Get-Content ~/.tolik/session-token) or TOLIK_SESSION_TOKEN env var.");
    return 0;
}

static async Task<int> RunListenAsync(CoreApiClient client, string[] args)
{
    byte[] audio;
    if (GetOption(args, "--file") is { } path and not null)
    {
        audio = VoiceIO.ReadWavFile(path);
    }
    else
    {
        Console.WriteLine("Speak after the beep. TOLIK listens until you press ENTER.");
        audio = VoiceIO.RecordUntilEnter();
    }

    var result = await client.VoiceTurnAsync(audio);
    if (!result.IsSuccess || result.Value is null)
    {
        Console.Error.WriteLine("Voice turn failed: " + (result.Error ?? result.StatusCode.ToString()));
        return 1;
    }

    Console.WriteLine("Вы:    " + result.Value.Transcription);
    Console.WriteLine("TOLIK: " + result.Value.ReplyText);
    if (result.Value.Audio is { Length: > 0 })
    {
        if (!GetFlag(args, "--quiet")) VoiceIO.PlayWav(result.Value.Audio);
    }
    else
    {
        Console.WriteLine("(TTS disabled for this mode — text only)");
    }
    return 0;
}

static async Task<int> RunRemindAsync(CoreApiClient client, string[] args)
{
    var text = GetOption(args, "--text");
    var delay = GetOption(args, "--delay");
    if (text is null || !int.TryParse(delay, out var seconds))
    {
        Console.Error.WriteLine("Usage: tolik remind --text TEXT --delay SECONDS");
        return 64;
    }
    return await PrintAsync(await client.RemindAsync(text, seconds));
}

static async Task<int> RunSoundAsync(CoreApiClient client, string[] args)
{
    var category = GetOption(args, "--category") ?? "reactions";
    var name = GetOption(args, "--name");
    if (name is null)
    {
        Console.Error.WriteLine("Usage: tolik sound --name FILE [--category reactions|memes|family|system]");
        return 64;
    }
    var result = await client.PlaySoundAsync(category, name);
    if (result.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
    {
        Console.Error.WriteLine("Rate limited: one sound per 10 seconds (§17).");
        return 1;
    }
    return await PrintAsync(result);
}

static bool GetFlag(string[] arguments, string name) =>
    arguments.Any(argument => string.Equals(argument, name, StringComparison.OrdinalIgnoreCase));

static void PrintUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  tolik status|modules|agents [--endpoint URL] [--token TOKEN]");
    Console.WriteLine("  tolik chat MESSAGE [--endpoint URL] [--token TOKEN]");
    Console.WriteLine("  tolik task create --description TEXT | get TASK_ID | cancel TASK_ID");
    Console.WriteLine("  tolik memory --query TEXT");
    Console.WriteLine("  tolik bootstrap --code TLK-XXXX");
    Console.WriteLine("  tolik transcribe --file PATH");
    Console.WriteLine("  tolik say --file PATH [--out PATH]");
    Console.WriteLine("  tolik listen [--file PATH] [--quiet]      (live mic -> reply, speaks answer)");
    Console.WriteLine("  tolik remind --text TEXT --delay SECONDS");
    Console.WriteLine("  tolik sound --name FILE [--category reactions|memes|family|system]");
}
