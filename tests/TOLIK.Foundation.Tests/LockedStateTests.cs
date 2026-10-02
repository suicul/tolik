using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TOLIK.Contracts;
using TOLIK.Infrastructure.Authorization;
using TOLIK.Infrastructure.Identity;

namespace TOLIK.Core.Host.Tests;

/// <summary>
/// LOCKED state machine (docs/SECURITY.md §27 Non-Destructive Lockdown; ported from
/// interface/policy.js coreState): a serious security event trips the core into
/// LOCKED — sensitive operations are denied outright (fail closed), authentication
/// is refused, and leaving LOCKED requires the owner's out-of-band unlock code
/// (never automatic, no role unlocks through the regular path).
/// </summary>
public sealed class LockedStateTests
{
    private static AuthorizationRequest Request(
        string? role,
        AgentCapability capability,
        ToolRiskLevel risk,
        string resource,
        bool confirmation = false,
        bool mfa = false,
        bool coreLocked = false) => new(
        Principal: new SessionPrincipal(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), role ?? string.Empty),
        Capability: capability,
        Risk: risk,
        Resource: resource,
        AllowedResource: resource,
        ConfirmationValid: confirmation,
        MfaValid: mfa,
        CoreLocked: coreLocked);

    [Fact]
    public void State_starts_operational()
    {
        var state = new CoreSecurityState();
        Assert.False(state.IsLocked);
        Assert.Null(state.Reason);
        Assert.Null(state.LockedAt);
    }

    [Fact]
    public void Enter_locked_records_reason_and_time()
    {
        var state = new CoreSecurityState();
        Assert.True(state.EnterLocked("brute_force_indicator:10"));
        Assert.True(state.IsLocked);
        Assert.Equal("brute_force_indicator:10", state.Reason);
        Assert.NotNull(state.LockedAt);
    }

    [Fact]
    public void Enter_locked_is_idempotent()
    {
        var state = new CoreSecurityState();
        Assert.True(state.EnterLocked("event-1"));
        var lockedAt = state.LockedAt;
        Assert.False(state.EnterLocked("event-2"));
        Assert.True(state.IsLocked);
        // First reason and timestamp preserved (evidence, not overwritten).
        Assert.Equal("event-1", state.Reason);
        Assert.Equal(lockedAt, state.LockedAt);
    }

    [Fact]
    public void Empty_reason_is_rejected()
    {
        var state = new CoreSecurityState();
        Assert.Throws<ArgumentException>(() => state.EnterLocked(""));
        Assert.Throws<ArgumentException>(() => state.EnterLocked("  "));
        Assert.Throws<ArgumentException>(() => state.TryUnlock(""));
        Assert.False(state.IsLocked);
    }

    [Fact]
    public void Try_unlock_requires_lock_and_is_explicit()
    {
        var state = new CoreSecurityState();
        Assert.False(state.TryUnlock("owner")); // unlock while operational → no-op
        state.EnterLocked("event");
        Assert.True(state.TryUnlock("owner action"));
        Assert.False(state.IsLocked);
        Assert.Null(state.Reason);
        Assert.Null(state.LockedAt);
    }

    [Fact]
    public void Locked_core_denies_every_operation()
    {
        // The LOCKED check is first in the engine: even Owner + read-only is denied,
        // and confirmation + MFA cannot rescue (§27 fail closed).
        Assert.Equal(AuthorizationDecision.Deny, AuthorizationPolicy.Evaluate(
            Request("Owner", AgentCapability.ReadFile, ToolRiskLevel.ReadOnly, "project-a", coreLocked: true)));
        Assert.Equal(AuthorizationDecision.Deny, AuthorizationPolicy.Evaluate(
            Request("Owner", AgentCapability.WriteFile, ToolRiskLevel.Modify, "project-a", confirmation: true, coreLocked: true)));
        Assert.Equal(AuthorizationDecision.Deny, AuthorizationPolicy.Evaluate(
            Request("Owner", AgentCapability.WriteFile, ToolRiskLevel.Critical, "project-a", confirmation: true, mfa: true, coreLocked: true)));
        Assert.Equal(AuthorizationDecision.Deny, AuthorizationPolicy.Evaluate(
            Request("AuthorizedUser", AgentCapability.RunBuild, ToolRiskLevel.LowRisk, "project-a", coreLocked: true)));
    }

    [Fact]
    public void Operational_core_is_unaffected()
    {
        Assert.Equal(AuthorizationDecision.Allow, AuthorizationPolicy.Evaluate(
            Request("Owner", AgentCapability.ReadFile, ToolRiskLevel.ReadOnly, "project-a", coreLocked: false)));
    }

    [Fact]
    public async Task Brute_force_failures_trip_locked_and_lock_refuses_everyone()
    {
        using var factory = new CoreHostFactory();
        using var scope = factory.Services.CreateScope();
        var identity = scope.ServiceProvider.GetRequiredService<IdentityService>();
        var securityState = scope.ServiceProvider.GetRequiredService<CoreSecurityState>();

        // 9 failures — below the default threshold (10), still operational.
        for (var i = 0; i < 9; i++)
        {
            Assert.Null(await identity.AuthenticateAsync($"invalid-token-{i}"));
            Assert.False(securityState.IsLocked);
        }
        // 10th failure crosses the threshold → LOCKED.
        Assert.Null(await identity.AuthenticateAsync("invalid-token-final"));
        Assert.True(securityState.IsLocked);
        Assert.Contains("brute_force_indicator", securityState.Reason, StringComparison.Ordinal);
        // Once locked, authentication is refused for everyone (fail closed).
        Assert.Null(await identity.AuthenticateAsync("anything"));
        Assert.True(securityState.IsLocked);
    }

    [Fact]
    public async Task Lock_and_unlock_endpoints_follow_the_owner_and_code_gates()
    {
        using var factory = new CoreHostFactory { UnlockCode = "test-unlock-code-2026" };
        using var client = factory.CreateClient();
        var sessionToken = await TestEnrollment.EnrollOwnerAsync(client);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", sessionToken);

        // Lock (owner) → 200.
        var lockResponse = await client.PostAsJsonAsync("/security/lock", new { reason = "manual_test_lock" });
        Assert.Equal(HttpStatusCode.OK, lockResponse.StatusCode);
        Assert.True((await lockResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("locked").GetBoolean());

        // While locked: authentication is refused → protected endpoint 401.
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", sessionToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/status")).StatusCode);

        // Unlock with a wrong code → 403, stays locked.
        var wrongUnlock = await client.PostAsJsonAsync("/security/unlock", new { unlockCode = "wrong-code" });
        Assert.Equal(HttpStatusCode.Forbidden, wrongUnlock.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/status")).StatusCode);

        // Unlock with the correct out-of-band code → 200, operational again.
        var unlock = await client.PostAsJsonAsync("/security/unlock", new { unlockCode = "test-unlock-code-2026" });
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var payload = await unlock.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(payload.GetProperty("locked").GetBoolean());
        Assert.True(payload.GetProperty("transitioned").GetBoolean());

        // /status works again.
        var status = await client.GetAsync("/status");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal("Healthy", (await status.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Unlock_without_code_fails_closed()
    {
        using var factory = new CoreHostFactory();
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/security/unlock", new { unlockCode = "anything" });
        // Unset TOLIK_SECURITY_UNLOCK_CODE → unlock impossible (403, fail closed).
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
