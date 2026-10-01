# TOLIK Authorization (normative)

Single source of truth for who may do what. Threat model lives in
`security.md`; this file defines the decision matrix. Code:
`src/TOLIK.Infrastructure/Authorization/`, enforcement in
`src/TOLIK.Core.Host/Program.cs` (`DispatchGuardedAsync`).

## Decision chain (every tool call)

```text
WHO ARE YOU? → ARE YOU AUTHENTICATED? → ARE YOU THE OWNER (or authorized role)?
→ ARE YOU AUTHORIZED FOR THIS ACTION? → IS ADDITIONAL CONFIRMATION REQUIRED?
→ EXECUTE → AUDIT
```

Fail-closed at every arrow. The LLM never authorizes; only the Core
authorization layer decides.

## Sensitivity categories (§16) → requirements

| Category | Tool risk levels | Authentication | Extra |
|---|---|---|---|
| LOW | `ReadOnly` (read/list/status) | session | none |
| MEDIUM | `LowRisk` (build/test/run/execute, sound) | session | none (rate-limited where annoying) |
| HIGH | `Modify`, `Destructive` (write, codex/opencode, security-path writes) | session, Owner/AuthorizedUser | explicit confirmation, single-use, 10-min TTL, bound to (task,step,capability,resource) |
| CRITICAL | security sources, unresolved critical | session + Owner | confirmation AND MFA — currently **always Deny** (`MfaValid:false` hardcoded at execute; see gap C4). Treat as denied until the MFA chain is closed. |

Guest role: always denied. Voice confidence alone never authorizes
(`IdentityEvidence`: authenticated user id must equal requested user id).

## Escalation rules (automatic upgrades)

- `WriteFile` onto build/policy/identity paths (`.git/hooks`, `nuget.config`,
  `Directory.Build.*`, `.tolik/*`, `policy.json`, `CODEOWNERS`) → Destructive + confirm.
- `WriteFile` onto security sources (`AuthorizationPolicy.cs`, `AuditWriter.cs`,
  `DeviceChallengeVerifier.cs`, …) → Critical + confirm.
- Any `RunCodex` → Modify + confirm (codex-autonomy rule).

## Confirmations

Single-use, SHA-256-bound, 10-minute expiry, idempotent issue. Approve path:
Owner role only; Critical approvals are hard-refused (`strong_auth_required`).
**Storage gap**: in-memory only — lost on restart (gap C5, fix: durable table).

## Path isolation

`TOLIK_ALLOWED_ROOTS` (allowlist, startup fails if empty). Deny substrings:
`.env`, `id_rsa`, `credentials`. Agent resolves symlinks to final targets,
fail-closed on unresolvable links.
