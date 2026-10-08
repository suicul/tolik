using TOLIK.Contracts;
using TOLIK.Infrastructure.Authorization;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// Capability Registry (docs/governance.md §1): the single source of declared risk.
/// The registry must cover every AgentCapability value exactly once and fail closed
/// on unknown values (DeclaredRisk → Critical, Find → null).
/// </summary>
public sealed class CapabilityRegistryTests
{
    [Fact]
    public void All_covers_every_AgentCapability_value_exactly_once()
    {
        var expected = Enum.GetValues<AgentCapability>().OrderBy(x => x).ToArray();
        var actual = CapabilityRegistry.All.Select(d => d.Id).OrderBy(x => x).ToArray();
        Assert.Equal(expected, actual);
        // Exactly once: no duplicate descriptors within the registry itself.
        Assert.Equal(expected.Length, CapabilityRegistry.All.Length);
    }

    [Fact]
    public void DeclaredRisk_matches_the_audit_map()
    {
        // The ConfirmationStore.DeclaredRiskFor map (AGENTS.md §11), audit 08.10.2026.
        Assert.Equal(ToolRiskLevel.ReadOnly, CapabilityRegistry.DeclaredRisk(AgentCapability.ReadFile));
        Assert.Equal(ToolRiskLevel.ReadOnly, CapabilityRegistry.DeclaredRisk(AgentCapability.ListDirectory));
        Assert.Equal(ToolRiskLevel.ReadOnly, CapabilityRegistry.DeclaredRisk(AgentCapability.GetProcessStatus));
        Assert.Equal(ToolRiskLevel.LowRisk, CapabilityRegistry.DeclaredRisk(AgentCapability.RunProcess));
        Assert.Equal(ToolRiskLevel.LowRisk, CapabilityRegistry.DeclaredRisk(AgentCapability.RunBuild));
        Assert.Equal(ToolRiskLevel.LowRisk, CapabilityRegistry.DeclaredRisk(AgentCapability.RunTests));
        Assert.Equal(ToolRiskLevel.LowRisk, CapabilityRegistry.DeclaredRisk(AgentCapability.PlaySound));
        Assert.Equal(ToolRiskLevel.LowRisk, CapabilityRegistry.DeclaredRisk(AgentCapability.CancelTask));
        Assert.Equal(ToolRiskLevel.Modify, CapabilityRegistry.DeclaredRisk(AgentCapability.WriteFile));
        Assert.Equal(ToolRiskLevel.Modify, CapabilityRegistry.DeclaredRisk(AgentCapability.RunCodex));
        Assert.Equal(ToolRiskLevel.Modify, CapabilityRegistry.DeclaredRisk(AgentCapability.RunOpenCode));
    }

    [Fact]
    public void DeclaredRisk_fails_closed_to_Critical_for_unknown_capability()
    {
        // No capability is declared Critical today; an unregistered value must fail closed.
        Assert.Equal(ToolRiskLevel.Critical, CapabilityRegistry.DeclaredRisk((AgentCapability)999));
    }

    [Fact]
    public void Find_returns_null_for_unregistered_capability()
    {
        Assert.Null(CapabilityRegistry.Find((AgentCapability)999));
    }

    [Fact]
    public void Find_returns_expected_confirmation_requirements()
    {
        // Modifying capabilities require confirmation; low-risk execution does not.
        Assert.True(CapabilityRegistry.Find(AgentCapability.WriteFile)!.RequiresConfirmation);
        Assert.True(CapabilityRegistry.Find(AgentCapability.RunCodex)!.RequiresConfirmation);
        Assert.True(CapabilityRegistry.Find(AgentCapability.RunOpenCode)!.RequiresConfirmation);
        Assert.False(CapabilityRegistry.Find(AgentCapability.RunProcess)!.RequiresConfirmation);
        Assert.False(CapabilityRegistry.Find(AgentCapability.RunBuild)!.RequiresConfirmation);
        Assert.False(CapabilityRegistry.Find(AgentCapability.ReadFile)!.RequiresConfirmation);
    }

    [Fact]
    public void Reversible_flags_match_the_registry_spec()
    {
        Assert.True(CapabilityRegistry.Find(AgentCapability.ReadFile)!.Reversible);
        Assert.True(CapabilityRegistry.Find(AgentCapability.ListDirectory)!.Reversible);
        Assert.True(CapabilityRegistry.Find(AgentCapability.GetProcessStatus)!.Reversible);
        Assert.True(CapabilityRegistry.Find(AgentCapability.RunProcess)!.Reversible);
        Assert.True(CapabilityRegistry.Find(AgentCapability.RunBuild)!.Reversible);
        Assert.True(CapabilityRegistry.Find(AgentCapability.RunTests)!.Reversible);
        Assert.True(CapabilityRegistry.Find(AgentCapability.WriteFile)!.Reversible);
        Assert.False(CapabilityRegistry.Find(AgentCapability.PlaySound)!.Reversible);
        Assert.False(CapabilityRegistry.Find(AgentCapability.CancelTask)!.Reversible);
        Assert.False(CapabilityRegistry.Find(AgentCapability.RunCodex)!.Reversible);
        Assert.False(CapabilityRegistry.Find(AgentCapability.RunOpenCode)!.Reversible);
    }
}
