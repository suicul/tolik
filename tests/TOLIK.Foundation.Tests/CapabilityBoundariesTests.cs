using TOLIK.Infrastructure.Authorization;
using Xunit;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// Phase H capability boundaries (docs/governance.md §9 — PLAN REFORM PRG-4c):
/// «посмотреть» ≠ «удалить» (the per-operation file tiers with the protected TOLIK
/// areas), the network destinations allowlist (the empty = localhost-only fail
/// closed) with the rate limiting, and the SECRET OPAQUE HANDLE (the LLM never sees
/// the values — the string form is ALWAYS the redaction; the unwrap is the
/// executor's explicit call). All deterministic — the policy cores for the
/// consuming paths' wiring.
/// </summary>
public sealed class FilePermissionPolicyTests
{
    private static readonly string[] Roots = [@"C:\DEV\TOLIK_QWEN"];

    [Fact]
    public void Read_and_write_are_the_roots_tier()
    {
        Assert.True(FilePermissionPolicy.Evaluate(FileOperation.Read, @"C:\DEV\TOLIK_QWEN\src\Program.cs", Roots).Allowed);
        Assert.True(FilePermissionPolicy.Evaluate(FileOperation.Write, @"C:\DEV\TOLIK_QWEN\src\Program.cs", Roots).Allowed);
    }

    [Fact]
    public void Outside_the_roots_denies_for_everything()
    {
        Assert.False(FilePermissionPolicy.Evaluate(FileOperation.Read, @"C:\Windows\System32\config.sys", Roots).Allowed);
        Assert.False(FilePermissionPolicy.Evaluate(FileOperation.Delete, @"C:\Windows\System32\config.sys", Roots).Allowed);
        Assert.False(FilePermissionPolicy.Evaluate(FileOperation.Write, @"C:\Users\other\Documents\file.txt", Roots).Allowed);
    }

    [Fact]
    public void Look_is_not_delete_protected_areas_never_move_or_delete()
    {
        // «Посмотреть» ≠ «удалить»: the .tolik identity data is readable (with the
        // ToolEscalation on the sensitive FILES) but NEVER moved or deleted by the file ops.
        Assert.True(FilePermissionPolicy.Evaluate(FileOperation.Read, @"C:\DEV\TOLIK_QWEN\.tolik\session-token", Roots).Allowed);
        Assert.False(FilePermissionPolicy.Evaluate(FileOperation.Delete, @"C:\DEV\TOLIK_QWEN\.tolik\session-token", Roots).Allowed);
        Assert.False(FilePermissionPolicy.Evaluate(FileOperation.Move, @"C:\DEV\TOLIK_QWEN\.git\HEAD", Roots).Allowed);
        Assert.False(FilePermissionPolicy.Evaluate(FileOperation.Delete, @"C:\DEV\TOLIK_QWEN\.env", Roots).Allowed);
        // A normal workspace file deletes fine.
        Assert.True(FilePermissionPolicy.Evaluate(FileOperation.Delete, @"C:\DEV\TOLIK_QWEN\temp\scratch.txt", Roots).Allowed);
    }

    [Fact]
    public void Execute_is_the_narrowest_tier()
    {
        Assert.True(FilePermissionPolicy.Evaluate(FileOperation.Execute, @"C:\DEV\TOLIK_QWEN\scripts\run.ps1", Roots).Allowed);
        Assert.False(FilePermissionPolicy.Evaluate(FileOperation.Execute, @"C:\DEV\TOLIK_QWEN\README.md", Roots).Allowed); // not an executable
        Assert.False(FilePermissionPolicy.Evaluate(FileOperation.Execute, @"C:\DEV\TOLIK_QWEN\.tolik\tool.exe", Roots).Allowed); // the protected area
    }

    [Fact]
    public void No_roots_or_bad_input_fails_closed()
    {
        Assert.False(FilePermissionPolicy.Evaluate(FileOperation.Read, @"C:\DEV\TOLIK_QWEN\src\Program.cs", []).Allowed);
        Assert.False(FilePermissionPolicy.Evaluate(FileOperation.Read, "  ", Roots).Allowed);
    }
}

public sealed class NetworkPolicyTests
{
    [Fact]
    public void Localhost_is_always_allowed_the_rest_fails_closed_without_a_list()
    {
        Assert.True(NetworkPolicy.Evaluate(new Uri("http://127.0.0.1:5080/health"), null).Allowed);
        Assert.True(NetworkPolicy.Evaluate(new Uri("http://localhost:9660/health"), "").Allowed);
        Assert.False(NetworkPolicy.Evaluate(new Uri("https://api.example.com/v1"), null).Allowed); // empty list = localhost only
    }

    [Fact]
    public void The_allowlist_matches_exact_and_wildcard_hosts()
    {
        var list = "api.openai.com, *.tooken.club";
        Assert.True(NetworkPolicy.Evaluate(new Uri("https://api.openai.com/v1/chat"), list).Allowed);
        Assert.True(NetworkPolicy.Evaluate(new Uri("https://eu.tooken.club/api"), list).Allowed);    // the wildcard subdomain
        Assert.False(NetworkPolicy.Evaluate(new Uri("https://evil.tooken.club.fake/api"), list).Allowed); // the suffix trick fails
        Assert.False(NetworkPolicy.Evaluate(new Uri("https://api.anthropic.com/v1"), list).Allowed);
    }

    [Fact]
    public void Null_or_relative_destination_fails_closed()
    {
        Assert.False(NetworkPolicy.Evaluate(null, "api.openai.com").Allowed);
        Assert.False(NetworkPolicy.Evaluate(new Uri("/relative", UriKind.Relative), "api.openai.com").Allowed);
    }

    [Fact]
    public void Rate_gate_caps_per_host_and_globally()
    {
        var gate = new NetworkRateGate();
        var t0 = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < NetworkRateGate.PerHost; i++)
            Assert.True(gate.TryConsume("api.openai.com", t0.AddSeconds(i)));
        Assert.False(gate.TryConsume("api.openai.com", t0.AddSeconds(30)));    // the host burst capped
        Assert.True(gate.TryConsume("other.example.org", t0.AddSeconds(31)));  // a different host untouched
        Assert.True(gate.TryConsume("api.openai.com", t0.AddSeconds(70)));     // the window expired
        Assert.False(gate.TryConsume("", t0));                                  // no host — fail closed
    }
}

public sealed class SecretAccessLayerTests
{
    [Fact]
    public void The_handle_string_form_is_always_the_redaction_never_the_value()
    {
        var handle = new SecretHandle("cloud_api_key", "sk-SECRET-VALUE-12345"u8.ToArray());
        var asString = handle.ToString();
        Assert.Equal("[secret:cloud_api_key]", asString);
        Assert.DoesNotContain("sk-SECRET", asString); // the value CANNOT string into a context — structurally
        // Interpolation, logging, error messages — every string path shows the redaction.
        Assert.DoesNotContain("sk-SECRET", $"{handle}");
        Assert.DoesNotContain("sk-SECRET", string.Concat(handle));
    }

    [Fact]
    public void The_unwrap_is_the_executors_explicit_call()
    {
        var handle = new SecretHandle("cloud_api_key", "sk-12345"u8.ToArray());
        Assert.Equal("sk-12345"u8.ToArray(), handle.UnwrapValue()); // the operation's edge
        var unwrapped = handle.UnwrapValue();
        Assert.Equal("sk-12345"u8.ToArray(), unwrapped); // the unwrap gives the real bytes
    }

    [Fact]
    public void The_declared_slots_whitelist_gates_the_requests()
    {
        var slots = "cloud_api_key, tooken_api_key";
        Assert.True(SecretPolicy.IsDeclaredSlot("cloud_api_key", slots));
        Assert.True(SecretPolicy.IsDeclaredSlot("TOOKEN_API_KEY", slots)); // case-insensitive
        Assert.False(SecretPolicy.IsDeclaredSlot("arbitrary_name", slots));  // never a free-form fetch
        Assert.False(SecretPolicy.IsDeclaredSlot(null, slots));
        Assert.False(SecretPolicy.IsDeclaredSlot("cloud_api_key", null)); // no slots declared — deny
    }
}
