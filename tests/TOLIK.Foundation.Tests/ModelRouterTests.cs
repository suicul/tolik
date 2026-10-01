using Microsoft.Extensions.Configuration;
using TOLIK.Infrastructure.Models;

namespace TOLIK.Foundation.Tests;

public sealed class ModelRouterTests
{
    private static ModelRoute Route() => new("fake", "model", new Uri("https://provider.invalid/v1/"), null);
    [Fact]
    public void Resolves_configured_route_without_hardcoding_provider()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TOLIK_MODELS_CODING:PROVIDER"] = "openai-compatible",
            ["TOLIK_MODELS_CODING:MODEL"] = "gpt-5.6-sol",
            ["TOLIK_MODELS_CODING:ENDPOINT"] = "https://provider.invalid/v1",
            ["TOLIK_MODELS_CODING:API_KEY"] = "test-key"
        }).Build();
        var route = new ModelRouter(config).Resolve(ModelTaskKind.Coding);
        Assert.Equal("openai-compatible", route!.Provider);
        Assert.Equal("gpt-5.6-sol", route.Model);
        Assert.Equal("test-key", route.ApiKey);
    }

    [Fact]
    public void Cloud_route_without_key_is_unconfigured()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TOLIK_MODELS_CODING:PROVIDER"] = "openai-compatible",
            ["TOLIK_MODELS_CODING:MODEL"] = "gpt-5.6-sol",
            ["TOLIK_MODELS_CODING:ENDPOINT"] = "https://provider.invalid/v1"
        }).Build();
        Assert.Null(new ModelRouter(config).Resolve(ModelTaskKind.Coding));
    }

    [Fact]
    public void Simple_prefers_offline_even_when_cloud_configured()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TOLIK_OFFLINE_MODE"] = "false",
            ["TOLIK_MODELS_SIMPLE:PROVIDER"] = "openai-compatible",
            ["TOLIK_MODELS_SIMPLE:MODEL"] = "cloud-chat",
            ["TOLIK_MODELS_SIMPLE:ENDPOINT"] = "https://provider.invalid/v1",
            ["TOLIK_MODELS_SIMPLE:API_KEY"] = "test-key",
            ["TOLIK_MODEL_OFFLINE_SIMPLE:MODEL"] = "qwen3:4b",
            ["TOLIK_MODEL_OFFLINE_SIMPLE:ENDPOINT"] = "http://localhost:11434"
        }).Build();
        var route = new ModelRouter(config).ResolveWithFallback(ModelTaskKind.Simple);
        Assert.Equal("qwen3:4b", route!.Model);
    }

    [Fact]
    public void Coding_falls_back_to_offline_without_key()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TOLIK_OFFLINE_MODE"] = "false",
            ["TOLIK_MODELS_CODING:PROVIDER"] = "openai-compatible",
            ["TOLIK_MODELS_CODING:MODEL"] = "gpt-5.6-sol",
            ["TOLIK_MODELS_CODING:ENDPOINT"] = "https://provider.invalid/v1",
            ["TOLIK_MODEL_OFFLINE:MODEL"] = "qwen3:4b",
            ["TOLIK_MODEL_OFFLINE:ENDPOINT"] = "http://localhost:11434"
        }).Build();
        var route = new ModelRouter(config).ResolveWithFallback(ModelTaskKind.Coding);
        Assert.Equal("qwen3:4b", route!.Model);
    }

    [Fact]
    public void Missing_or_invalid_route_fails_closed()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["TOLIK_MODELS_SIMPLE:MODEL"] = "x" }).Build();
        Assert.Null(new ModelRouter(config).Resolve(ModelTaskKind.Simple));
    }

    [Fact]
    public async Task Provider_http_failure_is_normalized()
    {
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.BadGateway)));
        var sut = new ModelCompletionClient(client, new OpenAiCompatibleRequestBuilder());
        await Assert.ThrowsAsync<ModelProviderUnavailableException>(() => sut.CompleteAsync(Route(), new ModelRequest("model", "hi"), TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Provider_timeout_is_normalized()
    {
        using var client = new HttpClient(new DelayHandler());
        var sut = new ModelCompletionClient(client, new OpenAiCompatibleRequestBuilder());
        await Assert.ThrowsAsync<ModelProviderUnavailableException>(() => sut.CompleteAsync(Route(), new ModelRequest("model", "hi"), TimeSpan.FromMilliseconds(10)));
    }

    [Fact]
    public void Think_flag_goes_only_to_ollama()
    {
        var builder = new OpenAiCompatibleRequestBuilder();
        var ollama = new ModelRoute("ollama", "m", new Uri("http://localhost:11434"), null);
        var cloud = new ModelRoute("openai-compatible", "m", new Uri("https://provider.invalid/v1/"), "k");
        Assert.True(ReadPayload(builder.Build(ollama, new ModelRequest("m", "hi", Think: false))).Contains("\"think\":false"));
        Assert.False(ReadPayload(builder.Build(cloud, new ModelRequest("m", "hi", Think: false))).Contains("think"));
        Assert.False(ReadPayload(builder.Build(ollama, new ModelRequest("m", "hi"))).Contains("think"));
    }

    [Fact]
    public void NumCtx_goes_into_options_for_ollama_only()
    {
        var builder = new OpenAiCompatibleRequestBuilder();
        var ollama = new ModelRoute("ollama", "m", new Uri("http://localhost:11434"), null);
        var payload = ReadPayload(builder.Build(ollama, new ModelRequest("m", "hi", NumPredict: 250, Think: false, NumCtx: 8192)));
        Assert.Contains("\"num_predict\":250", payload);
        Assert.Contains("\"num_ctx\":8192", payload);
        Assert.Contains("\"think\":false", payload);
    }

    private static string ReadPayload(HttpRequestMessage message) =>
        message.Content!.ReadAsStringAsync().GetAwaiter().GetResult();

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(handler(request));
    }
    private sealed class DelayHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { await Task.Delay(500, cancellationToken); return new HttpResponseMessage(System.Net.HttpStatusCode.OK); }
    }
}
