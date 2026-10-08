using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TOLIK.Contracts;

namespace TOLIK.Infrastructure.Authorization;

/// <summary>
/// Ported from interface/core.js (stage 2 of Option A, docs/INTERFACE-ANALYSIS.md):
/// confirmations are BOUND to a specific (task, step, capability, resource) via a
/// canonical SHA-256 hash, are single-use and expire — a confirmation for one step
/// can never authorize another step, and replays are rejected.
/// </summary>
public static class ConfirmationBinding
{
    public static string Hash(Guid taskId, int stepNumber, AgentCapability capability, string resource)
    {
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new { taskId, stepNumber, capability = capability.ToString(), resource }, new JsonSerializerOptions { WriteIndented = false });
        return Convert.ToHexString(SHA256.HashData(canonical));
    }
}

public sealed record PendingConfirmation(
    Guid Id,
    Guid TaskId,
    int StepNumber,
    AgentCapability Capability,
    string Resource,
    string BindingHash,
    ToolRiskLevel EffectiveRisk,
    string RequiredMethod,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt)
{
    public bool Approved { get; internal set; }
    public bool Consumed { get; internal set; }
    public bool Expired => DateTimeOffset.UtcNow > ExpiresAt;

    /// <summary>§9: critical operations require strong auth, not just a voice "yes".</summary>
    public bool RequiresMfa => EffectiveRisk == ToolRiskLevel.Critical;
}

public sealed class ConfirmationStore
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<Guid, PendingConfirmation> _items = new();

    /// <summary>Creates (idempotently) a pending confirmation for the binding. Returns the confirmation.</summary>
    public PendingConfirmation Issue(Guid taskId, int stepNumber, AgentCapability capability, string resource, ToolRiskLevel effectiveRisk)
    {
        var bindingHash = ConfirmationBinding.Hash(taskId, stepNumber, capability, resource);
        var existing = _items.Values.FirstOrDefault(x =>
            x.TaskId == taskId && x.BindingHash == bindingHash && !x.Consumed && !x.Expired);
        if (existing is not null) return existing;

        var confirmation = new PendingConfirmation(
            Id: Guid.NewGuid(),
            TaskId: taskId,
            StepNumber: stepNumber,
            Capability: capability,
            Resource: resource,
            BindingHash: bindingHash,
            EffectiveRisk: effectiveRisk,
            RequiredMethod: effectiveRisk == ToolRiskLevel.Critical ? "OwnerStrongAuth" : "ExplicitUserConfirm",
            CreatedAt: DateTimeOffset.UtcNow,
            ExpiresAt: DateTimeOffset.UtcNow.Add(DefaultLifetime));
        _items[confirmation.Id] = confirmation;
        return confirmation;
    }

    /// <summary>Owner approves a pending confirmation (does not consume it).</summary>
    public bool Approve(Guid confirmationId)
    {
        if (!_items.TryGetValue(confirmationId, out var confirmation)) return false;
        if (confirmation.Consumed || confirmation.Expired) return false;
        confirmation.Approved = true;
        return true;
    }

    /// <summary>
    /// Single-use consumption: succeeds only for an approved, non-expired, non-consumed
    /// confirmation whose binding hash matches the exact task/step/capability/resource.
    /// </summary>
    public bool Consume(Guid taskId, int stepNumber, AgentCapability capability, string resource)
    {
        var bindingHash = ConfirmationBinding.Hash(taskId, stepNumber, capability, resource);
        var confirmation = _items.Values.FirstOrDefault(x => x.TaskId == taskId && x.BindingHash == bindingHash);
        if (confirmation is null || !confirmation.Approved || confirmation.Consumed || confirmation.Expired)
            return false;
        confirmation.Consumed = true;
        return true;
    }

    public PendingConfirmation? Get(Guid confirmationId) =>
        _items.TryGetValue(confirmationId, out var confirmation) && !confirmation.Expired ? confirmation : null;

    public IReadOnlyList<PendingConfirmation> ListForTask(Guid taskId) =>
        _items.Values.Where(x => x.TaskId == taskId && !x.Consumed && !x.Expired).ToList();

    /// <summary>
    /// Declares the default (declared) risk per capability (AGENTS.md §11); delegates to
    /// the Capability Registry (docs/governance.md §1) — the single source of declared risk.
    /// </summary>
    public static ToolRiskLevel DeclaredRiskFor(AgentCapability capability) => CapabilityRegistry.DeclaredRisk(capability);
}

/// <summary>Thrown inside the execute loop when a step needs explicit confirmation.</summary>
public sealed class ConfirmationRequiredException(PendingConfirmation confirmation) : Exception($"confirmation_required:{confirmation.Id}")
{
    public PendingConfirmation Confirmation { get; } = confirmation;
}
