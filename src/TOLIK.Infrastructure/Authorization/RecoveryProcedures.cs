using TOLIK.Contracts;

namespace TOLIK.Infrastructure.Authorization;

/// <summary>
/// RecoveryProcedure (docs/governance.md §4, Phase D/D2 — PLAN REFORM PRG-4d): the
/// TYPED registry of the REGISTERED recovery procedures — only these exist; a
/// self-healing action NEVER improvises («попробуй случайные admin-команды» is
/// exactly what this registry makes impossible). The unknown trigger finds nothing
/// (fail closed) and the caller STOPS → explains → asks the owner.
/// </summary>
public enum RecoveryTrigger
{
    VoiceServiceDown,
    TtsDown,
    ComponentDead,
    DatabaseUnavailable,
    TaskStuck
}

public sealed record RecoveryProcedure(
    RecoveryTrigger Trigger,
    string Name,                    // the fixed procedure literal
    AgentCapability Action,         // the EXISTING capability the action routes through
    string VerificationHint,        // the fixed health-check hint
    int MaxAttempts)
{
    public bool IsValid => MaxAttempts >= 1 && !string.IsNullOrWhiteSpace(Name);
}

public static class RecoveryRegistry
{
    /// <summary>The REGISTERED procedures — the fixed set, extendable by registration
    /// only (never improvisation). Each routes through an EXISTING capability: the
    /// authorization pipeline stays mandatory for every recovery action.</summary>
    public static readonly IReadOnlyList<RecoveryProcedure> All =
    [
        new(RecoveryTrigger.VoiceServiceDown, "restart_voice_service", AgentCapability.RunProcess,
            "verify: the voice service /health returns 200", MaxAttempts: 2),
        new(RecoveryTrigger.TtsDown, "fallback_tts_provider", AgentCapability.PlaySound,
            "verify: the TTS chain falls back (XTTS -> edge -> offline) and speaks", MaxAttempts: 2),
        new(RecoveryTrigger.ComponentDead, "controlled_component_restart", AgentCapability.RunProcess,
            "verify: the component's health endpoint reports alive", MaxAttempts: 2),
        new(RecoveryTrigger.DatabaseUnavailable, "restore_database_known", AgentCapability.RunProcess,
            "verify: pg_isready + the core /health memory=true", MaxAttempts: 1),
        new(RecoveryTrigger.TaskStuck, "supervisor_stall_path", AgentCapability.CancelTask,
            "verify: the CA-2 supervisor ladder (L1->L2->L3 pause)", MaxAttempts: 2)
    ];

    /// <summary>Finds the registered procedure; unknown = null (fail closed — the
    /// caller STOPS, explains honestly, and asks the owner; never improvises).</summary>
    public static RecoveryProcedure? Find(RecoveryTrigger trigger) =>
        All.FirstOrDefault(p => p.Trigger == trigger);
}

/// <summary>
/// The bounded recovery attempt governance: per-trigger attempts capped (2 by
/// default — the procedure's MaxAttempts); the exhausted trigger refuses new
/// attempts (STOP → explain → ask the owner — § D) until explicitly re-armed
/// (the owner's decision, or the successful verification resets). Thread-safe.
/// Never authorizes anything — the recovery action still passes the authorization
/// pipeline through its registered capability.
/// </summary>
public sealed class RecoveryAttemptGate
{
    private readonly object _gate = new();
    private readonly Dictionary<RecoveryTrigger, (int Attempts, DateTimeOffset LastAt)> _attempts = new();

    public bool TryBegin(RecoveryProcedure procedure, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(procedure);
        lock (_gate)
        {
            var (attempts, _) = _attempts.GetValueOrDefault(procedure.Trigger);
            if (attempts >= procedure.MaxAttempts) return false; // exhausted → STOP → explain → ask
            _attempts[procedure.Trigger] = (attempts + 1, now);
            return true;
        }
    }

    /// <summary>The successful verification resets the trigger (the component healed).</summary>
    public void MarkResolved(RecoveryTrigger trigger)
    {
        lock (_gate) { _attempts.Remove(trigger); }
    }

    /// <summary>The owner's explicit re-arm (the fresh chance after the STOP).</summary>
    public void Rearm(RecoveryTrigger trigger)
    {
        lock (_gate) { _attempts.Remove(trigger); }
    }
}

/// <summary>
/// InstallTolikUpdate integrity policy (Phase D2): a self-update installs ONLY from
/// a SIGNED artifact — the manifest with the artifact SHA256 + the signature is
/// MANDATORY; anything else rejects fail-closed (the unsigned/broken/tampered
/// update NEVER installs — T13). The signature's cryptographic verification itself
/// is the wiring step (the owner-held key); this policy is the deterministic gate
/// the installer must consult first. PURE, unit-tested.
/// </summary>
public sealed record UpdateManifest(string ArtifactName, string Sha256, string Signature)
{
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(ArtifactName) &&
        !string.IsNullOrWhiteSpace(Sha256) &&
        !string.IsNullOrWhiteSpace(Signature);
}

public static class UpdateIntegrityPolicy
{
    public static (bool Allowed, string? Reason) Evaluate(UpdateManifest? manifest)
    {
        if (manifest is null)
            return (false, "manifest: required — an unsigned blob never installs.");
        if (!manifest.IsValid)
            return (false, "manifest: the artifact name, the SHA256, and the signature are ALL mandatory.");
        if (manifest.Sha256.Length != 64 || manifest.Sha256.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            return (false, "sha256: a 64-char lowercase hex digest required.");
        return (true, null); // the manifest is structurally valid; the SIGNATURE VERIFICATION
                             // (the owner-held key) is the installer's mandatory next step —
                             // this gate passing is necessary but NEVER sufficient alone.
    }
}
