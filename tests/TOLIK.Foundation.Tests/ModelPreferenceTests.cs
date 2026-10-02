using Microsoft.Extensions.Configuration;
using TOLIK.Infrastructure.Models;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// Remembered per-user model-routing preference (docs/ROADMAP.md model policy,
/// ask-policy item): a remembered Local preference sends coding requests to the
/// offline model first; Cloud keeps the cloud-first default; no preference leaves
/// the router unchanged. Per-user isolation; the store is in-memory per process.
/// </summary>
public sealed class ModelPreferenceTests
{
    private static IConfiguration Config(bool cloudConfigured = true) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["TOLIK_OFFLINE_MODE"] = "false",
        ["TOLIK_MODELS_CODING:PROVIDER"] = "openai-compatible",
        ["TOLIK_MODELS_CODING:MODEL"] = "gpt-5.6-sol",
        ["TOLIK_MODELS_CODING:ENDPOINT"] = "https://provider.invalid/v1",
        ["TOLIK_MODELS_CODING:API_KEY"] = cloudConfigured ? "test-key" : null,
        ["TOLIK_MODEL_OFFLINE:MODEL"] = "qwen3:4b",
        ["TOLIK_MODEL_OFFLINE:ENDPOINT"] = "http://localhost:11434"
    }).Build();

    [Fact]
    public void No_preference_leaves_the_router_unchanged()
    {
        var store = new ModelPreferenceStore();
        Assert.Null(store.Get(Guid.NewGuid()));
        var route = new ModelRouter(Config()).ResolveWithFallback(ModelTaskKind.Coding, store.Get(Guid.NewGuid()));
        Assert.Equal("gpt-5.6-sol", route!.Model);
    }

    [Fact]
    public void Remembered_local_preference_sends_coding_offline_first()
    {
        var store = new ModelPreferenceStore();
        var userId = Guid.NewGuid();
        store.Set(userId, ModelRoutingPreference.Local);
        var route = new ModelRouter(Config()).ResolveWithFallback(ModelTaskKind.Coding, store.Get(userId));
        Assert.Equal("qwen3:4b", route!.Model);
    }

    [Fact]
    public void Remembered_cloud_preference_keeps_cloud_first()
    {
        var store = new ModelPreferenceStore();
        var userId = Guid.NewGuid();
        store.Set(userId, ModelRoutingPreference.Cloud);
        var route = new ModelRouter(Config()).ResolveWithFallback(ModelTaskKind.Coding, store.Get(userId));
        Assert.Equal("gpt-5.6-sol", route!.Model);
    }

    [Fact]
    public void Local_preference_still_falls_back_to_cloud_when_offline_unconfigured()
    {
        // A remembered Local preference cannot break the assistant: no offline route
        // configured → cloud still answers (fail open on availability, not security).
        var store = new ModelPreferenceStore();
        var userId = Guid.NewGuid();
        store.Set(userId, ModelRoutingPreference.Local);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TOLIK_OFFLINE_MODE"] = "false",
            ["TOLIK_MODELS_CODING:PROVIDER"] = "openai-compatible",
            ["TOLIK_MODELS_CODING:MODEL"] = "gpt-5.6-sol",
            ["TOLIK_MODELS_CODING:ENDPOINT"] = "https://provider.invalid/v1",
            ["TOLIK_MODELS_CODING:API_KEY"] = "test-key"
        }).Build();
        var route = new ModelRouter(config).ResolveWithFallback(ModelTaskKind.Coding, store.Get(userId));
        Assert.Equal("gpt-5.6-sol", route!.Model);
    }

    [Fact]
    public void Simple_kind_is_unaffected_by_the_preference()
    {
        // Simple (chatter) already prefers local first — the preference is a
        // coding-route override, not a chatter override.
        var store = new ModelPreferenceStore();
        var userId = Guid.NewGuid();
        store.Set(userId, ModelRoutingPreference.Cloud);
        var route = new ModelRouter(Config()).ResolveWithFallback(ModelTaskKind.Simple, store.Get(userId));
        Assert.Equal("qwen3:4b", route!.Model);
    }

    [Fact]
    public void Preferences_are_isolated_per_user_and_overwritable()
    {
        var store = new ModelPreferenceStore();
        var ivan = Guid.NewGuid();
        var other = Guid.NewGuid();
        store.Set(ivan, ModelRoutingPreference.Cloud);
        store.Set(other, ModelRoutingPreference.Local);
        Assert.Equal(ModelRoutingPreference.Cloud, store.Get(ivan));
        Assert.Equal(ModelRoutingPreference.Local, store.Get(other));
        store.Set(ivan, ModelRoutingPreference.Local);
        Assert.Equal(ModelRoutingPreference.Local, store.Get(ivan));
    }

    [Fact]
    public void Clear_returns_to_the_default_ask_again_state()
    {
        var store = new ModelPreferenceStore();
        var userId = Guid.NewGuid();
        store.Set(userId, ModelRoutingPreference.Local);
        store.Clear(userId);
        Assert.Null(store.Get(userId));
        var route = new ModelRouter(Config()).ResolveWithFallback(ModelTaskKind.Coding, store.Get(userId));
        Assert.Equal("gpt-5.6-sol", route!.Model);
    }

    [Fact]
    public void Empty_user_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new ModelPreferenceStore().Set(Guid.Empty, ModelRoutingPreference.Cloud));
    }
}
