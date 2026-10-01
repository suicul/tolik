using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace TOLIK.Infrastructure.Models;

public sealed record OllamaChatMessage(string Role, string Content);
public sealed record OllamaMessage(string Role, string Content);
public sealed record OllamaRequest(
    string Model,
    string Prompt,
    bool Stream = false,
    int? KeepAlive = null);
public sealed record OllamaChatResponse(
    string CreatedAt,
    OllamaMessage Message,
    string Model,
    bool Done,
    long TotalDuration,
    long LoadDuration,
    int PromptEvalCount,
    long EvalCount,
    long EvalDuration);

public sealed class OllamaProvider : IModelProviderAdapter
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly Uri _endpoint;

    public string Provider => "ollama";

    public OllamaProvider(HttpClient http, OllamaOptions options)
    {
        _http = http;
        _model = options.Model;
        _endpoint = options.Endpoint;
    }

    public async Task<string> CompleteAsync(
        ModelRoute route,
        ModelRequest request,
        CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(3));

        var payload = new OllamaRequest(_model, request.Prompt, Stream: false);
        using var msg = new HttpRequestMessage(HttpMethod.Post, new Uri(_endpoint, "api/chat"));
        msg.Content = JsonContent.Create(payload);

        using var response = await _http.SendAsync(msg, cts.Token);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("message", out var msgEl) &&
            msgEl.TryGetProperty("content", out var contentEl))
        {
            return contentEl.GetString() ?? string.Empty;
        }

        throw new InvalidOperationException("Ollama response missing 'message.content' field.");
    }
}

public sealed record OllamaOptions(string Model, Uri Endpoint);
