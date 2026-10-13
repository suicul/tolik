namespace TOLIK.Contracts;

/// <summary>
/// The Privilege Broker protocol (docs/governance.md §3, Phase C/C0 — PLAN REFORM
/// PRG-4f): TYPED operations ONLY — there is structurally NO string-command field
/// anywhere in this protocol («ExecuteAsAdmin(string command)» is exactly what
/// these types make unrepresentable — tested by reflection). The broker serves ONE
/// MFA-confirmed operation at a time; the remote clients (Quest/Android) NEVER talk
/// to it directly — the chain is Core → PC Agent → Broker (a local-machine
/// capability).
/// </summary>
public enum BrokerOp
{
    /// <summary>The liveness probe — the broker answers with its policy state.</summary>
    HealthCheck = 0,

    /// <summary>Restarts one of the FIXED-allowlist services (the RecoveryRegistry's
    /// known names). The target must match the allowlist exactly — never free-form.</summary>
    RestartService = 1,

    /// <summary>The arbitrary elevated command (Phase C's explicitly-gated escape):
    /// DENY BY DEFAULT — exists in the capability registry (CRITICAL + MFA + Owner)
    /// but the broker refuses it unless TOLIK_BROKER_ALLOW_ELEVATE is explicitly set
    /// AND the full authorization pipeline confirmed it. The double gate.</summary>
    ElevateCommand = 2
}

public sealed record BrokerRequest(BrokerOp Op, string? Target, Guid RequestId)
{
    public bool IsValid => RequestId != Guid.Empty &&
        (Op != BrokerOp.RestartService || !string.IsNullOrWhiteSpace(Target));
}

public sealed record BrokerResponse(bool Ok, string? Detail, Guid RequestId);

/// <summary>
/// The broker-side allowlist (docs/governance.md §3): the FIXED set of the typed
/// targets the RestartService op accepts — the RecoveryRegistry's known procedure
/// names, nothing else. An unknown target denies (fail closed).
/// </summary>
public static class BrokerAllowlist
{
    public static readonly IReadOnlyList<string> RestartableServices =
    [
        "restart_voice_service",
        "controlled_component_restart"
    ];

    public static bool IsRestartable(string? target) =>
        !string.IsNullOrWhiteSpace(target) &&
        RestartableServices.Contains(target.Trim(), StringComparer.Ordinal);
}
