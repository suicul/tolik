using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace TOLIK.Cli;

public sealed class CoreApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient httpClient;
    private readonly string? sessionToken;

    public CoreApiClient(HttpClient httpClient, Uri endpoint, string? sessionToken)
    {
        httpClient.BaseAddress = endpoint;
        this.httpClient = httpClient;
        this.sessionToken = string.IsNullOrWhiteSpace(sessionToken) ? null : sessionToken;
    }

    public Task<ApiResponse<StatusResponse>> GetStatusAsync(CancellationToken cancellationToken = default) => SendAsync<StatusResponse>(new ApiRequest(HttpMethod.Get, "/status"), cancellationToken);
    public Task<ApiResponse<ModuleStatusResponse>> GetModulesAsync(CancellationToken cancellationToken = default) => SendAsync<ModuleStatusResponse>(new ApiRequest(HttpMethod.Get, "/status/modules"), cancellationToken);
    public Task<ApiResponse<ChatResponse>> SendChatAsync(string message, CancellationToken cancellationToken = default) => SendAsync<ChatResponse>(new ApiRequest(HttpMethod.Post, "/chat", new ChatRequest(message)), cancellationToken);
    public Task<ApiResponse<TaskResponse>> CreateTaskAsync(string description, CancellationToken cancellationToken = default) => SendAsync<TaskResponse>(new ApiRequest(HttpMethod.Post, "/tasks", new TaskRequest(description)), cancellationToken);
    public Task<ApiResponse<TaskResponse>> GetTaskAsync(Guid taskId, CancellationToken cancellationToken = default) => SendAsync<TaskResponse>(new ApiRequest(HttpMethod.Get, $"/tasks/{taskId:D}"), cancellationToken);
    public Task<ApiResponse<TaskResponse>> CancelTaskAsync(Guid taskId, CancellationToken cancellationToken = default) => SendAsync<TaskResponse>(new ApiRequest(HttpMethod.Post, $"/tasks/{taskId:D}/cancel"), cancellationToken);
    public Task<ApiResponse<TranscribeResponse>> TranscribeFileAsync(byte[] audio, CancellationToken cancellationToken = default) => SendAsync<TranscribeResponse>(new ApiRequest(HttpMethod.Post, "/voice/transcribe", new TranscribeRequest(audio)), cancellationToken);
    public Task<ApiResponse<VoiceTurnResponse>> VoiceTurnAsync(byte[] audio, CancellationToken cancellationToken = default) => SendAsync<VoiceTurnResponse>(new ApiRequest(HttpMethod.Post, "/voice/turn", new VoiceTurnRequest(audio)), cancellationToken);
    public Task<ApiResponse<MemorySearchResponse>> SearchMemoryAsync(string query, CancellationToken cancellationToken = default) => SendAsync<MemorySearchResponse>(new ApiRequest(HttpMethod.Get, "/memory/search?q=" + Uri.EscapeDataString(query)), cancellationToken);
    public Task<ApiResponse<AgentsResponse>> GetAgentsAsync(CancellationToken cancellationToken = default) => SendAsync<AgentsResponse>(new ApiRequest(HttpMethod.Get, "/agents"), cancellationToken);
    public Task<ApiResponse<RemindResponse>> RemindAsync(string text, int delaySeconds, CancellationToken cancellationToken = default) => SendAsync<RemindResponse>(new ApiRequest(HttpMethod.Post, "/proactive/remind", new RemindRequest(text, delaySeconds)), cancellationToken);
    public Task<ApiResponse<SoundPlayResponse>> PlaySoundAsync(string category, string name, CancellationToken cancellationToken = default) => SendAsync<SoundPlayResponse>(new ApiRequest(HttpMethod.Post, "/audio/play", new PlaySoundRequest(category, name)), cancellationToken);
    public Task<ApiResponse<JsonElement>> GetAudioLibraryAsync(CancellationToken cancellationToken = default) => SendAsync<JsonElement>(new ApiRequest(HttpMethod.Get, "/audio/library"), cancellationToken);

    public async Task<ApiResponse<EnrollResponse>?> BootstrapEnrollAsync(string bootstrapCode, string installationId, System.Security.Cryptography.ECDsa key, CancellationToken cancellationToken = default)
    {
        var challenge = await SendAsync<ChallengeResponse>(new ApiRequest(HttpMethod.Post, "/auth/bootstrap/challenge", new BootstrapChallengeRequest(bootstrapCode)), cancellationToken);
        if (!challenge.IsSuccess || challenge.Value is null)
            return new ApiResponse<EnrollResponse>(challenge.StatusCode, null, challenge.Error ?? "challenge_failed");

        var challengeBytes = System.Text.Encoding.UTF8.GetBytes(challenge.Value.Challenge ?? string.Empty);
        var signature = Convert.ToBase64String(key.SignData(challengeBytes, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.DSASignatureFormat.Rfc3279DerSequence));
        // PEM must keep its newlines — ImportFromPem on the server expects the standard layout.
        var publicKey = key.ExportSubjectPublicKeyInfoPem();

        return await SendAsync<EnrollResponse>(new ApiRequest(HttpMethod.Post, "/auth/bootstrap/enroll", new EnrollRequest(bootstrapCode, installationId, publicKey, challenge.Value.Challenge, signature)), cancellationToken);
    }

    private async Task<ApiResponse<T>> SendAsync<T>(ApiRequest request, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(request.Method, request.Path);
        if (request.Payload is not null) message.Content = JsonContent.Create(request.Payload, options: JsonOptions);
        if (sessionToken is not null) message.Headers.Authorization = new("Bearer", sessionToken);
        using var response = await httpClient.SendAsync(message, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var value = TryDeserialize<T>(body);
        var error = response.IsSuccessStatusCode ? null : TryReadError(body);
        return new ApiResponse<T>(response.StatusCode, value, error);
    }

    private static T? TryDeserialize<T>(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return default;
        try { return JsonSerializer.Deserialize<T>(body, JsonOptions); }
        catch (JsonException) { return default; }
    }

    private static string? TryReadError(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error)) return error.GetString();
            if (document.RootElement.TryGetProperty("detail", out var detail)) return detail.GetString();
            return document.RootElement.TryGetProperty("title", out var title) ? title.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private sealed record ApiRequest(HttpMethod Method, string Path, object? Payload = null);
}

public sealed record ApiResponse<T>(HttpStatusCode StatusCode, T? Value, string? Error)
{
    public bool IsSuccess => (int)StatusCode is >= 200 and < 300;
    public bool IsAuthorizationFailure => StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
}

public sealed record StatusResponse(string? Status, string? Role, Guid? UserId, Guid? DeviceId);
public sealed record ChatRequest(string Message);
public sealed record ChatResponse(string? Answer, string? Model, string? Action, string? Details);
public sealed record TaskRequest(string Description);
public sealed record TaskResponse(Guid Id, string? Description, string? Status);
public sealed record ModuleStatusResponse(string? Core, string? Memory, string? Voice, bool VoiceAvailable, int Agents);
public sealed record BootstrapChallengeRequest(string BootstrapCode);
public sealed record ChallengeResponse(string? Challenge, DateTimeOffset? ExpiresAt);
public sealed record EnrollRequest(string BootstrapCode, string InstallationId, string PublicKey, string? Challenge, string? Signature);
public sealed record EnrollResponse(Guid UserId, Guid DeviceId, Guid SessionId, string? SessionToken);
public sealed record TranscribeRequest(byte[] Audio);
public sealed record TranscribeResponse(string? Text, string? Language, double DurationSeconds, string? Model);
public sealed record VoiceTurnRequest(byte[] Audio);
public sealed record VoiceTurnResponse(string? Transcription, string? ReplyText, string? Mode, byte[]? Audio, string? ContentType, string? Model);
public sealed record MemorySearchResponse(object? Local, IReadOnlyList<MemoryEntry>? Persisted);
public sealed record MemoryEntry(Guid Id, string? Content, string? Source, double Confidence, DateTimeOffset CreatedAt);
public sealed record AgentsResponse(IReadOnlyList<AgentEntry>? Agents);
public sealed record AgentEntry(Guid AgentId, string Status, DateTimeOffset ConnectedAt, IReadOnlyList<string>? Capabilities);
public sealed record RemindRequest(string? Text, int DelaySeconds);
public sealed record RemindResponse(Guid Id, string? Text, DateTimeOffset DueAt, string? Priority);
public sealed record PlaySoundRequest(string? Category, string? Name);
public sealed record SoundPlayResponse(bool Played, string? Sound, string? Detail);
