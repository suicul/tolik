using TOLIK.Contracts;
using TOLIK.Infrastructure.Authorization;
using Xunit;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// PLAN-ЭТАЛОН Phase 1.1 / 1.5 — the risk ladder's strong-auth step.
///
/// The dispatcher used to call AuthorizationPolicy with a hardcoded `MfaValid: false`. That
/// constant looked like a security check and behaved like a kill switch: for Critical
/// capabilities the policy requires confirmation AND MFA, so InstallTolikUpdate could never
/// be authorized — TOLIK's self-update was structurally impossible while the code appeared
/// to be protecting it. Meanwhile the approve endpoint refused every Critical confirmation
/// with an unconditional 403 and no way to satisfy it, so the protection was not real
/// either: nothing could ever pass.
///
/// These tests pin the honest behaviour: the MFA fact is produced by a verified TOTP, it is
/// time-bounded, it is single-use, and it is what reaches the policy.
/// </summary>
public sealed class RiskLadderMfaTests
{
    private const string Resource = "C:\\DEV\\TOLIK_QWEN";

    private static PendingConfirmation Issue(ConfirmationStore store, ToolRiskLevel risk)
    {
        var taskId = Guid.NewGuid();
        return store.Issue(taskId, stepNumber: 1, AgentCapability.InstallTolikUpdate, Resource, risk);
    }

    /// <summary>
    /// A Critical confirmation cannot be approved without an MFA proof. Fail closed: an
    /// approval without strong auth would let the dispatcher see "approved" and the policy
    /// see "no MFA", which is exactly the incoherent pair this phase removes.
    /// </summary>
    [Fact]
    public void Critical_approval_without_mfa_fails_closed()
    {
        var store = new ConfirmationStore();
        var confirmation = Issue(store, ToolRiskLevel.Critical);

        Assert.False(store.Approve(confirmation.Id));
        Assert.False(confirmation.Approved);
        Assert.Null(confirmation.MfaVerifiedAt);

        var consumption = store.Consume(confirmation.TaskId, 1, confirmation.Capability, confirmation.Resource);
        Assert.False(consumption.Valid);
        Assert.False(consumption.MfaValid);
    }

    /// <summary>A real proof, recent enough, authorizes the step and reports MFA as valid.</summary>
    [Fact]
    public void Critical_approval_with_a_fresh_mfa_proof_authorizes_the_step()
    {
        var store = new ConfirmationStore();
        var confirmation = Issue(store, ToolRiskLevel.Critical);

        Assert.True(store.Approve(confirmation.Id, DateTimeOffset.UtcNow));
        var consumption = store.Consume(confirmation.TaskId, 1, confirmation.Capability, confirmation.Resource);

        Assert.True(consumption.Valid);
        Assert.True(consumption.MfaValid);
    }

    /// <summary>
    /// Strong auth must be CLOSE IN TIME to the act it authorizes. A proof that has aged
    /// past the lifetime still leaves the confirmation valid (the owner did approve) but
    /// stops counting as MFA — so a Critical step is denied rather than silently allowed on
    /// an old code.
    /// </summary>
    [Fact]
    public void A_stale_mfa_proof_stops_counting()
    {
        var store = new ConfirmationStore();
        var confirmation = Issue(store, ToolRiskLevel.Critical);

        var stale = DateTimeOffset.UtcNow - ConfirmationStore.MfaProofLifetime - TimeSpan.FromSeconds(1);
        Assert.True(store.Approve(confirmation.Id, stale));

        var consumption = store.Consume(confirmation.TaskId, 1, confirmation.Capability, confirmation.Resource);
        Assert.True(consumption.Valid);
        Assert.False(consumption.MfaValid);
    }

    /// <summary>Non-Critical risks never require MFA — the ladder must not over-demand.</summary>
    [Fact]
    public void Destructive_approval_needs_no_mfa()
    {
        var store = new ConfirmationStore();
        var confirmation = Issue(store, ToolRiskLevel.Destructive);

        Assert.False(confirmation.RequiresMfa);
        Assert.True(store.Approve(confirmation.Id));

        var consumption = store.Consume(confirmation.TaskId, 1, confirmation.Capability, confirmation.Resource);
        Assert.True(consumption.Valid);
        Assert.False(consumption.MfaValid); // no proof was ever given — reported honestly
    }

    /// <summary>Single use: the second consumption of the same binding authorizes nothing.</summary>
    [Fact]
    public void A_confirmation_authorizes_exactly_one_step()
    {
        var store = new ConfirmationStore();
        var confirmation = Issue(store, ToolRiskLevel.Critical);
        Assert.True(store.Approve(confirmation.Id, DateTimeOffset.UtcNow));

        Assert.True(store.Consume(confirmation.TaskId, 1, confirmation.Capability, confirmation.Resource).Valid);
        var replay = store.Consume(confirmation.TaskId, 1, confirmation.Capability, confirmation.Resource);
        Assert.False(replay.Valid);
        Assert.False(replay.MfaValid);
    }

    /// <summary>A confirmation for one step never authorizes a different step.</summary>
    [Fact]
    public void A_confirmation_is_bound_to_its_step()
    {
        var store = new ConfirmationStore();
        var confirmation = Issue(store, ToolRiskLevel.Critical);
        Assert.True(store.Approve(confirmation.Id, DateTimeOffset.UtcNow));

        var otherStep = store.Consume(confirmation.TaskId, 2, confirmation.Capability, confirmation.Resource);
        Assert.False(otherStep.Valid);
    }

    /// <summary>
    /// 1.5 — the guard that makes the defect unable to come back. If someone reintroduces
    /// the constant, this test fails before the behaviour silently regresses.
    /// </summary>
    [Fact]
    public void The_dispatcher_passes_a_real_mfa_fact_not_a_constant()
    {
        var programPath = Path.Combine(FindRepositoryRoot(), "src", "TOLIK.Core.Host", "Program.cs");
        Assert.True(File.Exists(programPath), $"Program.cs not found at {programPath}.");
        var source = File.ReadAllText(programPath);

        Assert.DoesNotContain("MfaValid: false", source, StringComparison.Ordinal);
        Assert.Contains("MfaValid: consumption.MfaValid", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "TOLIK.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the TOLIK repository root.");
    }
}
