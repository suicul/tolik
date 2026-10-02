using TOLIK.Contracts;
using TOLIK.Infrastructure.Authorization;
using TOLIK.Infrastructure.Identity;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// Authorization leases (normative: docs/INTERFACE-ANALYSIS.md; ported from
/// interface/identity.js issueLease/validateLease/revokeLease): short-lived per-run
/// authorization with a TTL (60s default), revocation and single-use authorization
/// consumption. Unknown/revoked/expired leases fail closed. Offline mode caps the
/// effective risk band at read-only (no world-modification, no rescue by confirmation).
/// </summary>
public sealed class LeaseTests
{
    private static AuthorizationLeaseStore Store() => new();

    private static AuthorizationRequest Request(
        string? role,
        AgentCapability capability,
        ToolRiskLevel risk,
        string resource,
        bool confirmation = false,
        bool mfa = false,
        bool offline = false,
        DateTimeOffset? authorizationExpiresAt = null,
        DateTimeOffset? evaluatedAt = null) => new(
        Principal: new SessionPrincipal(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), role ?? string.Empty),
        Capability: capability,
        Risk: risk,
        Resource: resource,
        AllowedResource: resource,
        ConfirmationValid: confirmation,
        MfaValid: mfa,
        OfflineMode: offline,
        AuthorizationExpiresAt: authorizationExpiresAt,
        EvaluatedAt: evaluatedAt);

    private static readonly DateTimeOffset FixedNow = new(2026, 10, 2, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Issued_lease_validates_within_ttl()
    {
        var store = Store();
        var lease = store.Issue(Guid.NewGuid(), ToolRiskLevel.Modify, now: FixedNow);
        Assert.NotNull(store.Validate(lease.Id, FixedNow.AddSeconds(59)));
    }

    [Fact]
    public void Lease_expires_after_ttl_and_authorizes_nothing()
    {
        var store = Store();
        var lease = store.Issue(Guid.NewGuid(), ToolRiskLevel.Modify, now: FixedNow);
        Assert.Null(store.Validate(lease.Id, FixedNow.Add(AuthorizationLeaseStore.DefaultTtl)));
        Assert.Null(store.Validate(lease.Id, FixedNow.AddSeconds(61)));
        // Expired lease expiry also denies through the policy path (EvaluatedAt injection
        // keeps the assertion deterministic — no dependence on the real clock).
        Assert.Equal(AuthorizationDecision.Deny, AuthorizationPolicy.Evaluate(
            Request("Owner", AgentCapability.WriteFile, ToolRiskLevel.Modify, "project-a",
                confirmation: true, authorizationExpiresAt: lease.ExpiresAt,
                evaluatedAt: lease.ExpiresAt.AddSeconds(1))));
    }

    [Fact]
    public void Unknown_lease_id_fails_closed()
    {
        Assert.Null(Store().Validate(Guid.NewGuid(), FixedNow));
    }

    [Fact]
    public void Revoked_lease_never_validates_again()
    {
        var store = Store();
        var lease = store.Issue(Guid.NewGuid(), ToolRiskLevel.Modify, now: FixedNow);
        Assert.True(store.Revoke(lease.Id, "task_cancelled"));
        Assert.Null(store.Validate(lease.Id, FixedNow));
        Assert.Equal("task_cancelled", store.Get(lease.Id)!.RevokedReason);
    }

    [Fact]
    public void Authorization_consumption_is_single_use_per_lease()
    {
        var store = Store();
        var lease = store.Issue(Guid.NewGuid(), ToolRiskLevel.Modify, now: FixedNow);
        Assert.True(lease.ConsumeAuthorization("1:ReadFile:project-a"));
        Assert.False(lease.ConsumeAuthorization("1:ReadFile:project-a"));
        // A different lease is unaffected (per-lease isolation).
        var other = store.Issue(Guid.NewGuid(), ToolRiskLevel.Modify, now: FixedNow);
        Assert.True(other.ConsumeAuthorization("1:ReadFile:project-a"));
    }

    [Fact]
    public void Lease_ceiling_defaults_to_readonly_for_unparsed_roles()
    {
        // Unknown role names fail closed: the lease ceiling cannot exceed read-only.
        var store = Store();
        var lease = store.Issue(Guid.NewGuid(), TolikRoles.CeilingFor(TolikRoles.Parse("Superuser") ?? IdentityRole.Guest) ?? ToolRiskLevel.ReadOnly, now: FixedNow);
        Assert.Equal(ToolRiskLevel.ReadOnly, lease.RiskCeiling);
    }

    [Fact]
    public void Offline_mode_caps_effective_risk_at_read_only_band()
    {
        // Modify declared → denied outright offline, confirmation cannot rescue.
        Assert.Equal(AuthorizationDecision.Deny, AuthorizationPolicy.Evaluate(
            Request("Owner", AgentCapability.WriteFile, ToolRiskLevel.Modify, "project-a", confirmation: true, offline: true)));
        // Destructive declared → denied offline.
        Assert.Equal(AuthorizationDecision.Deny, AuthorizationPolicy.Evaluate(
            Request("Owner", AgentCapability.WriteFile, ToolRiskLevel.Destructive, "project-a", confirmation: true, offline: true)));
        // Read-only band stays allowed offline.
        Assert.Equal(AuthorizationDecision.Allow, AuthorizationPolicy.Evaluate(
            Request("Owner", AgentCapability.ReadFile, ToolRiskLevel.ReadOnly, "project-a", offline: true)));
        Assert.Equal(AuthorizationDecision.Allow, AuthorizationPolicy.Evaluate(
            Request("Owner", AgentCapability.RunBuild, ToolRiskLevel.LowRisk, "project-a", offline: true)));
    }

    [Fact]
    public void Offline_cap_applies_to_escalated_effective_risk_not_declared()
    {
        // LowRisk-declared write onto .git/hooks escalates to Destructive — above the
        // offline cap → hard Deny even though the declared risk is LowRisk.
        Assert.Equal(AuthorizationDecision.Deny, AuthorizationPolicy.Evaluate(
            Request("Owner", AgentCapability.WriteFile, ToolRiskLevel.LowRisk, "repo/.git/hooks/pre-commit", offline: true)));
    }

    [Fact]
    public void Online_mode_is_unaffected_by_the_offline_cap()
    {
        Assert.Equal(AuthorizationDecision.Allow, AuthorizationPolicy.Evaluate(
            Request("Owner", AgentCapability.WriteFile, ToolRiskLevel.Modify, "project-a", confirmation: true, offline: false)));
    }
}
