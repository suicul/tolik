using Microsoft.Extensions.Configuration;
using System.Net.Http.Json;

namespace TOLIK.Infrastructure.Models;

public enum ModelTaskKind { Simple, Reasoning, Coding, Vision }
public sealed record ModelRoute(string Provider, string Model, Uri Endpoint, string? ApiKey);
public sealed record ModelRequest(string Model, string Prompt, int? NumPredict = null, bool? Think = null, int? NumCtx = null);
public interface IModelProviderAdapter
{
    string Provider { get; }
    Task<string> CompleteAsync(ModelRoute route, ModelRequest request, CancellationToken cancellationToken = default);
}

public sealed class OpenAiCompatibleRequestBuilder
{
    public HttpRequestMessage Build(ModelRoute route, ModelRequest request)
    {
        var baseEndpoint = route.Endpoint.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? route.Endpoint
            : new Uri(route.Endpoint.AbsoluteUri + "/");
        var isOllama = string.Equals(route.Provider, "ollama", StringComparison.OrdinalIgnoreCase);

        // Ollama: native /api/chat supports keep_alive (the OpenAI-compatible /v1
        // endpoint silently ignores it), keeping the model warm between turns —
        // cold-loading a 2 GB model takes 30-60s and would time out every first response.
        var endpoint = isOllama
            ? new Uri(new Uri(route.Endpoint.AbsoluteUri.Replace("/v1", "/")), "api/chat")
            : new Uri(baseEndpoint, "chat/completions");
        var message = new HttpRequestMessage(HttpMethod.Post, endpoint);
        if (!string.IsNullOrWhiteSpace(route.ApiKey))
            message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", route.ApiKey);

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["messages"] = new[] { new { role = "user", content = request.Prompt } }
        };
        if (isOllama)
        {
            payload["stream"] = false;
            payload["keep_alive"] = "60m";
            // Bound worst-case latency for short answers (Simple voice/chat);
            // null = provider default (coding/summaries stay unbounded).
            var options = new Dictionary<string, object?>();
            if (request.NumPredict.HasValue)
                options["num_predict"] = request.NumPredict.Value;
            // Cap context: Ollama defaults Qwen3 to 256k+ ctx, which blows VRAM
            // into CPU offload (~18 tok/s observed); 8k keeps 100% GPU (~100 tok/s).
            if (request.NumCtx.HasValue)
                options["num_ctx"] = request.NumCtx.Value;
            if (options.Count > 0)
                payload["options"] = options;
            // Qwen3 thinks by default (100+ reasoning tokens per greeting):
            // disable for latency paths, keep for deep tasks. Ollama-only flag.
            if (request.Think.HasValue)
                payload["think"] = request.Think.Value;
        }
        message.Content = JsonContent.Create(payload);
        return message;
    }
}

public sealed class ModelProviderUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class ModelCompletionClient(HttpClient httpClient, OpenAiCompatibleRequestBuilder builder)
{
    public async Task<string> CompleteAsync(
        ModelRoute route,
        ModelRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        using var message = builder.Build(route, request);
        try
        {
            using var response = await httpClient.SendAsync(message, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
                throw new ModelProviderUnavailableException($"Provider returned {(int)response.StatusCode}.");
            var raw = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            return ExtractAssistantText(raw);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ModelProviderUnavailableException("Provider request timed out.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ModelProviderUnavailableException("Provider request failed.", ex);
        }
    }

    /// <summary>
    /// Extracts the plain assistant text from either provider response shape:
    /// OpenAI {"choices":[{"message":{"content":...}}]} or Ollama native {"message":{"content":...}}.
    /// Falls back to the raw body when neither shape matches (custom providers).
    /// </summary>
    private static string ExtractAssistantText(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == System.Text.Json.JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var first = choices[0];
                if (first.TryGetProperty("message", out var openAiMessage) && openAiMessage.TryGetProperty("content", out var content))
                    return content.GetString() ?? string.Empty;
            }
            if (root.TryGetProperty("message", out var ollamaMessage) && ollamaMessage.TryGetProperty("content", out var ollamaContent))
                return ollamaContent.GetString() ?? string.Empty;
            // Qwen3 thinking models may return reasoning + content fields.
            if (root.TryGetProperty("response", out var plainResponse) && plainResponse.ValueKind == System.Text.Json.JsonValueKind.String)
                return plainResponse.GetString() ?? string.Empty;
        }
        catch (System.Text.Json.JsonException)
        {
            // Not JSON — treat the body as plain text.
        }
        return raw;
    }
}

public sealed class ModelRouter(IConfiguration configuration)
{
    public ModelRoute? Resolve(ModelTaskKind kind)
    {
        var offlineMode = configuration.GetValue<bool>("TOLIK_OFFLINE_MODE", false);
        if (offlineMode)
        {
            var offline = ResolveOffline(kind);
            if (offline is not null) return offline;
        }

        var prefix = $"TOLIK_MODELS_{kind.ToString().ToUpperInvariant()}";
        var provider = configuration[$"{prefix}:PROVIDER"];
        var model = configuration[$"{prefix}:MODEL"];
        var endpoint = configuration[$"{prefix}:ENDPOINT"];
        var apiKey = configuration[$"{prefix}:API_KEY"];
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model) || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            return null;
        // Cloud without a key is unconfigured (fail closed): lets ResolveWithFallback
        // fall back to offline instead of sending an unauthenticated request. Ollama needs no key.
        if (!string.Equals(provider, "ollama", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(apiKey))
            return null;
        return new ModelRoute(provider, model, uri, apiKey);
    }

    public ModelRoute? ResolveOffline(ModelTaskKind kind)
    {
        // Per-kind offline models (§5): fast small model for chat/reasoning,
        // the heavy coding model only for coding tasks. Falls back to the global
        // TOLIK_MODEL_OFFLINE section for unspecified kinds.
        var prefix = $"TOLIK_MODEL_OFFLINE_{kind.ToString().ToUpperInvariant()}";
        var model = configuration[$"{prefix}:MODEL"];
        var endpoint = configuration[$"{prefix}:ENDPOINT"];
        if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(endpoint))
        {
            model = configuration["TOLIK_MODEL_OFFLINE:MODEL"];
            endpoint = configuration["TOLIK_MODEL_OFFLINE:ENDPOINT"];
        }
        if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(endpoint))
            return null;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            return null;
        var provider = configuration[$"{prefix}:PROVIDER"] ?? configuration["TOLIK_MODEL_OFFLINE:PROVIDER"] ?? "ollama";
        return new ModelRoute(provider, model, uri, null);
    }

    /// <summary>
    /// Cloud-first with offline fallback (§5, §6): returns the route for the task kind,
    /// falling back to the offline model when the cloud route is unconfigured.
    /// Exception: Simple (chat/voice latency path) prefers the local model first —
    /// chatter stays on the home GPU even when cloud is enabled for the rest.
    /// A remembered per-user preference (ask-policy, docs/ROADMAP.md model policy)
    /// overrides the coding-kind default: Local → offline-first, Cloud → cloud-first.
    /// Never returns a route when neither is available.
    /// </summary>
    public ModelRoute? ResolveWithFallback(ModelTaskKind kind, ModelRoutingPreference? preference = null)
    {
        if (kind == ModelTaskKind.Simple)
            return ResolveOffline(kind) ?? Resolve(kind);
        if (preference is ModelRoutingPreference.Local)
            return ResolveOffline(kind) ?? Resolve(kind);
        var primary = Resolve(kind);
        if (primary is not null) return primary;
        var offline = ResolveOffline(kind);
        if (offline is not null) return offline;
        // Last resort: any configured route keeps the assistant answering.
        return Resolve(ModelTaskKind.Simple) is { } simple ? simple : null;
    }

    public bool IsOfflineMode => configuration.GetValue<bool>("TOLIK_OFFLINE_MODE", false);
}

/// <summary>
/// Heuristic task-kind selection for chat prompts (deterministic, no LLM).
/// Coding/debug signals switch to the Coding route; everything else stays Simple.
/// </summary>
public static class ChatModelSelector
{
    private static readonly string[] CodingSignals =
    [
        "код", "кодинг", "ошибк", "exception", "стектрейс", "stack trace", "compile", "сборк", "билд",
        "csproj", "sln", "regex", "sql", "запрос", "баг", "дебаг", "debug", "refactor", "рефактор",
        "функци", "метод", "класс c", "классифик", "linq", "asp.net", "dotnet", "миграци", "api"
    ];

    public static ModelTaskKind Pick(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return ModelTaskKind.Simple;
        var lowered = message.ToLowerInvariant();
        return CodingSignals.Any(signal => lowered.Contains(signal, StringComparison.Ordinal))
            ? ModelTaskKind.Coding
            : ModelTaskKind.Simple;
    }
}
