namespace TOLIK.Infrastructure.Authorization;

/// <summary>
/// Phase H capability boundaries (docs/governance.md §9 — PLAN REFORM PRG-4c):
/// the per-operation FILE permissions («посмотреть» ≠ «удалить»), the NETWORK
/// destinations allowlist with the rate limiting, and the SECRET access layer
/// (the LLM never sees the secret values — they flow to the executor opaquely).
/// All three are DETERMINISTIC, pure, unit-tested — the enforcement wiring lands
/// with the consuming paths; these are the policy cores.
/// </summary>

// ─────────────────────── H-Files: the per-operation tiers ───────────────────────

public enum FileOperation
{
    Read,
    Write,
    Move,
    Delete,
    Execute
}

public sealed record FilePermissionVerdict(bool Allowed, string? Reason = null);

/// <summary>
/// The per-operation file permission tiers (docs/governance.md §9 Files): the
/// operations are SEPARATE permissions — Read < Write < Move/Delete < Execute.
/// «Посмотреть» ≠ «удалить»: the destructive operations never touch the protected
/// TOLIK areas (.tolik / .env / .git — the identity data, the secrets, the repo
/// integrity; the defense-in-depth alongside ToolEscalation); EXECUTE is the
/// narrowest tier (the workspaces only, the known executable extensions). The
/// system areas deny for everything. The roots allowlist is the FLOOR (the
/// AgentPathGuard resolves the links — this policy owns the OPERATIONS on top).
/// </summary>
public static class FilePermissionPolicy
{
    /// <summary>The protected areas — never moved/deleted/executed by the file ops.</summary>
    public static readonly IReadOnlyList<string> ProtectedMarkers =
    [
        $"{Path.DirectorySeparatorChar}.tolik{Path.DirectorySeparatorChar}",
        $"{Path.AltDirectorySeparatorChar}.tolik{Path.AltDirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}.env",
        $"{Path.AltDirectorySeparatorChar}.env",
        $"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}",
        $"{Path.AltDirectorySeparatorChar}.git{Path.AltDirectorySeparatorChar}"
    ];

    private static readonly string[] ExecutableExtensions = [".exe", ".ps1", ".bat", ".cmd", ".msi", ".sh", ".py"];

    public static FilePermissionVerdict Evaluate(FileOperation operation, string fullPath, IReadOnlyList<string> allowedRoots)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return new(false, "path: required.");
        if (allowedRoots is null || allowedRoots.Count == 0) return new(false, "roots: no allowed roots configured — deny.");
        var full = Path.GetFullPath(fullPath);
        var insideRoots = allowedRoots.Any(root =>
        {
            var canonical = Path.GetFullPath(root);
            return full.Equals(canonical, StringComparison.OrdinalIgnoreCase) ||
                   full.StartsWith(canonical + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                   full.StartsWith(canonical + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        });
        if (!insideRoots)
            return new(false, "roots: the path is outside the allowed roots — deny (the system areas deny by construction).");

        var protectedArea = ProtectedMarkers.Any(m => full.Contains(m, StringComparison.OrdinalIgnoreCase));

        return operation switch
        {
            FileOperation.Read => new(true), // «посмотреть» — the roots suffice
            FileOperation.Write => new(true), // the write tier == the roots (the sensitive FILES stay escalated via ToolEscalation)
            FileOperation.Move or FileOperation.Delete => protectedArea
                ? new(false, $"protected: the TOLIK areas (.tolik/.env/.git) are never moved or deleted by the file operations — ask the owner.")
                : new(true),
            FileOperation.Execute => (protectedArea, IsExecutable(full)) switch
            {
                (true, _) => new(false, "protected: the TOLIK areas are never executed from."),
                (_, false) => new(false, $"execute: only the known executable extensions ({string.Join(", ", ExecutableExtensions)}) — never data files."),
                _ => new(true)
            },
            _ => new(false, "unknown operation — fail closed.")
        };
    }

    private static bool IsExecutable(string path) =>
        ExecutableExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}

// ─────────────────────── H-Network: the destinations allowlist ───────────────────────

public sealed record NetworkVerdict(bool Allowed, string? Reason = null);

/// <summary>
/// The network capability policy (docs/governance.md §9 Network): the DESTINATIONS
/// allowlist (TOLIK_NETWORK_ALLOWLIST — hosts/domains, comma-separated, case-
/// insensitive; a wildcard-subdomain "*.example.com" supported). The credentials
/// never reach the LLM (§ H-Secrets); the remote clients ride the existing
/// device/session/revocation model. An EMPTY allowlist = localhost only (the
/// fail-closed default — the network capability opens explicitly, never by omission).
/// </summary>
public static class NetworkPolicy
{
    public static NetworkVerdict Evaluate(Uri? destination, string? allowlist)
    {
        if (destination is null) return new(false, "destination: required.");
        if (!destination.IsAbsoluteUri) return new(false, "destination: an absolute URI required.");
        var host = destination.Host.ToLowerInvariant();
        if (host is "localhost" or "127.0.0.1" or "::1")
            return new(true, "localhost: always allowed.");
        if (string.IsNullOrWhiteSpace(allowlist))
            return new(false, "allowlist: empty — localhost only (the network opens explicitly, never by omission).");
        foreach (var raw in allowlist.Split(',', ';'))
        {
            var entry = raw.Trim().ToLowerInvariant();
            if (entry.Length == 0) continue;
            if (entry.StartsWith("*."))
            {
                var domain = entry[1..]; // ".example.com"
                if (host.EndsWith(domain, StringComparison.Ordinal))
                    return new(true, $"allowed: the wildcard {entry}.");
            }
            else if (host == entry)
            {
                return new(true, "allowed: the exact host.");
            }
        }
        return new(false, $"allowlist: the host {host} is not allowed.");
    }
}

/// <summary>
/// The per-host network rate limiting (the NotificationGate window pattern): the
/// sliding minute window per host + the global cap (TOLIK_NETWORK_PER_HOST_PER_
/// MINUTE / _GLOBAL_PER_MINUTE, fail-closed defaults 10/30). The over-limit denies
/// are honest. Thread-safe; in-memory (the runtime network budget).
/// </summary>
public sealed class NetworkRateGate
{
    public const int DefaultPerHost = 10;
    public const int DefaultGlobal = 30;

    private readonly object _gate = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _history = new(StringComparer.OrdinalIgnoreCase);

    public static int PerHost =>
        int.TryParse(Environment.GetEnvironmentVariable("TOLIK_NETWORK_PER_HOST_PER_MINUTE"), out var v) && v >= 1 && v <= 1000 ? v : DefaultPerHost;
    public static int Global =>
        int.TryParse(Environment.GetEnvironmentVariable("TOLIK_NETWORK_GLOBAL_PER_MINUTE"), out var v) && v >= 1 && v <= 1000 ? v : DefaultGlobal;

    public bool TryConsume(string host, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        var key = host.ToLowerInvariant();
        lock (_gate)
        {
            if (OverLimit(key, now, PerHost)) return false;
            if (OverLimit("*", now, Global)) return false;
            Record(key, now);
            Record("*", now);
            return true;
        }
    }

    private bool OverLimit(string key, DateTimeOffset now, int perMinute)
    {
        if (!_history.TryGetValue(key, out var queue)) return false;
        var cutoff = now.AddMinutes(-1);
        while (queue.Count > 0 && queue.Peek() < cutoff) queue.Dequeue();
        return queue.Count >= perMinute;
    }

    private void Record(string key, DateTimeOffset now)
    {
        if (!_history.TryGetValue(key, out var queue))
        {
            queue = new Queue<DateTimeOffset>();
            _history[key] = queue;
        }
        queue.Enqueue(now);
    }
}

// ─────────────────────── H-Secrets: the opaque handle layer ───────────────────────

/// <summary>
/// The SECRET ACCESS LAYER (docs/governance.md §9 Secrets): the LLM never sees the
/// secret values — PRINCIPLED, not merely filtered. A secret flows as an OPAQUE
/// SecretHandle: its string form is ALWAYS the redacted "[secret:name]" (a handle
/// that leaks into a prompt, a log, or an error message shows the redaction, never
/// the value); the value unwraps ONLY via the explicit UnwrapValue — called by the
/// executor at the last moment (the operation receives the bytes; the model context
/// structurally cannot). The declared names whitelist (TOLIK_SECRET_SLOTS) gates the
/// requests; unknown names fail closed.
/// </summary>
public sealed class SecretHandle
{
    public string Name { get; }
    private readonly byte[] _value;

    public SecretHandle(string name, byte[] value)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Secret name is required.", nameof(name));
        Name = name;
        _value = value ?? [];
    }

    /// <summary>ALWAYS the redacted form — the handle can never string into a context.</summary>
    public override string ToString() => $"[secret:{Name}]";

    /// <summary>The executor's explicit unwrap — the only way the value leaves the
    /// handle; called at the operation's edge, never in the prompt-building paths.</summary>
    public byte[] UnwrapValue() => _value.ToArray();
}

public static class SecretPolicy
{
    /// <summary>The declared secret slots (TOLIK_SECRET_SLOTS, comma-separated) — the
    /// requests for anything else fail closed (never a free-form secret fetch).</summary>
    public static bool IsDeclaredSlot(string? name, string? slots)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(slots)) return false;
        return slots.Split(',', ';').Any(s => string.Equals(s.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}
