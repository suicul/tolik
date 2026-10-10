namespace TOLIK.Contracts;

/// <summary>
/// The formal description of what TOLIK can do (docs/governance.md §1): static data,
/// the single source of declared risk. New capabilities receive a descriptor at
/// registration; an advertised↔declared mismatch fails closed.
/// </summary>
public sealed record CapabilityDescriptor(
    AgentCapability Id,
    string Category,
    ToolRiskLevel DeclaredRisk,
    bool RequiresConfirmation,
    bool RequiresMfa,
    bool AllowsBackground,
    bool AllowsSelfInvocation,
    bool Reversible,
    bool AuditAlways);

/// <summary>
/// Static capability registry (docs/governance.md §1): code = data — every
/// AgentCapability value has exactly one descriptor. Carries only the declared-risk
/// map; escalation rules stay in ToolEscalation (TOLIK.Infrastructure) and are NOT
/// re-implemented here. Unknown capabilities fail closed: DeclaredRisk → Critical,
/// Find → null (AGENTS.md §14).
/// </summary>
public static class CapabilityRegistry
{
    /// <summary>All registered capabilities — covers every AgentCapability value exactly once.</summary>
    public static readonly CapabilityDescriptor[] All =
    [
        new(AgentCapability.ReadFile, "Files", ToolRiskLevel.ReadOnly,
            RequiresConfirmation: false, RequiresMfa: false, AllowsBackground: true,
            AllowsSelfInvocation: false, Reversible: true, AuditAlways: true),
        new(AgentCapability.ListDirectory, "Files", ToolRiskLevel.ReadOnly,
            RequiresConfirmation: false, RequiresMfa: false, AllowsBackground: true,
            AllowsSelfInvocation: false, Reversible: true, AuditAlways: true),
        new(AgentCapability.GetProcessStatus, "System", ToolRiskLevel.ReadOnly,
            RequiresConfirmation: false, RequiresMfa: false, AllowsBackground: true,
            AllowsSelfInvocation: false, Reversible: true, AuditAlways: true),
        new(AgentCapability.RunProcess, "Local", ToolRiskLevel.LowRisk,
            RequiresConfirmation: false, RequiresMfa: false, AllowsBackground: false,
            AllowsSelfInvocation: true, Reversible: true, AuditAlways: true),
        new(AgentCapability.RunBuild, "Local", ToolRiskLevel.LowRisk,
            RequiresConfirmation: false, RequiresMfa: false, AllowsBackground: false,
            AllowsSelfInvocation: false, Reversible: true, AuditAlways: true),
        new(AgentCapability.RunTests, "Local", ToolRiskLevel.LowRisk,
            RequiresConfirmation: false, RequiresMfa: false, AllowsBackground: false,
            AllowsSelfInvocation: false, Reversible: true, AuditAlways: true),
        new(AgentCapability.PlaySound, "Voice", ToolRiskLevel.LowRisk,
            RequiresConfirmation: false, RequiresMfa: false, AllowsBackground: true,
            AllowsSelfInvocation: false, Reversible: false, AuditAlways: true),
        new(AgentCapability.CancelTask, "System", ToolRiskLevel.LowRisk,
            RequiresConfirmation: false, RequiresMfa: false, AllowsBackground: false,
            AllowsSelfInvocation: false, Reversible: false, AuditAlways: true),
        new(AgentCapability.CaptureScreen, "Perception", ToolRiskLevel.LowRisk,
            RequiresConfirmation: false, RequiresMfa: false, AllowsBackground: true,
            AllowsSelfInvocation: false, Reversible: false, AuditAlways: true),
        new(AgentCapability.WriteFile, "Files", ToolRiskLevel.Modify,
            RequiresConfirmation: true, RequiresMfa: false, AllowsBackground: false,
            AllowsSelfInvocation: false, Reversible: true, AuditAlways: true),
        new(AgentCapability.RunCodex, "Local", ToolRiskLevel.Modify,
            RequiresConfirmation: true, RequiresMfa: false, AllowsBackground: false,
            AllowsSelfInvocation: false, Reversible: false, AuditAlways: true),
        new(AgentCapability.RunOpenCode, "Local", ToolRiskLevel.Modify,
            RequiresConfirmation: true, RequiresMfa: false, AllowsBackground: false,
            AllowsSelfInvocation: false, Reversible: false, AuditAlways: true),
    ];

    /// <summary>Returns the descriptor for a capability, or null when unregistered — callers fail closed.</summary>
    public static CapabilityDescriptor? Find(AgentCapability capability) =>
        All.FirstOrDefault(d => d.Id == capability);

    /// <summary>Returns the declared risk of a capability; Critical for unknown values — fail closed.</summary>
    public static ToolRiskLevel DeclaredRisk(AgentCapability capability) =>
        Find(capability)?.DeclaredRisk ?? ToolRiskLevel.Critical;
}
