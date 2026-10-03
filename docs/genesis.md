# TOLIK Genesis

One-time birth ceremony of a permanent instance. Status: **ceremony subsystem
implemented** (Phase 6 done 03.10.2026: preflight, instance identity table, owner
binding, freeze/lock, atomicity, rerun impossibility — tests green) + **PRE-GENESIS
hardening pass done 03.10.2026 on live**: миграция применена, memory separation gate
satisfied (0 development-строк), genesis gates verified fail-closed on live, validated
pre-genesis backup taken (backups/tolik-backup-pre-genesis.dump, SHA-256) + restore
safety test PASS (fresh target restored, corrupt rejected — restore tested = FACT). The ceremony itself is NOT yet run: live is in DEVELOPMENT
lifecycle state with dev tokens on (parity) — turning them off + operator attestations
+ MFA код + запуск ceremony = переход к READY_FOR_GENESIS → LIFETIME.

## Model

```text
GENESIS START → Preflight → Verify environment → Verify database →
Verify backups → Verify owner authentication → Verify MFA →
Verify authorization → Verify memory separation → Create TOLIK identity →
Create owner binding → Freeze Genesis state → Write Genesis audit event →
LIFETIME
```

If interrupted: no half-born state — recovery rolls back to `READY_FOR_GENESIS`.

## Preflight gates (all must pass)

- No dev credentials anywhere (`TOLIK_DEV_AUTO_TOKEN` off/unset, no test sessions).
- Owner authenticated via bootstrap device + MFA verified.
- Backup exists, restore tested, important data survives.
- Memory separation verified: no `origin=development` rows outside test fixtures.
- Authorization matrix verified (tests green).

## Identity created at Genesis

```text
instance_id        new GUID, never reused, never equal to a name
born_at            UTC timestamp of the ceremony
genesis_version    ceremony protocol version (starts at 1)
code_identity      git commit hash of the baseline
knowledge_identity hash of the imported initial knowledge set
lifecycle_state    LIFETIME (persisted, see lifecycle.md)
owner_identity     user id bound at ceremony
```

## After Genesis (normative)

Forbidden without the security workflow (MFA + owner presence + audit):
memory reset, owner replacement, re-bootstrap, silent history deletion,
audit disable/tamper. The workflow itself is defined in `authorization.md`
and `security.md`.

## What exists today toward this

- Single-owner enrollment with consumed bootstrap (no second Owner).
- Device/session revocation.
- Hash-chained append-only audit (can record the ceremony event).
- Validated backup/restore scripts.
- Speaker enrollment (owner binding factor candidate).

## What is missing (Phase 6)

Preflight checks — DONE 03.10.2026 (`GenesisPreflight`: owner + MFA verified inline,
memory separation — 0 `origin=development` rows в production DB, dev-token флаг off,
runtime authority; backup/restore — operator attestations, фиксируются в ceremony event).
Instance identity table — DONE 03.10.2026 (`tolik_genesis_identity`, миграция
20261002174934, unique sentinel `LifecycleState` = rerun impossibility).
Owner binding ceremony step — DONE 03.10.2026 (`OwnerIdentity` в identity row,
re-bootstrap заблокирован после ceremony: challenge-gate + defense-in-depth в EnrollOwnerAsync).
Freeze/lock mechanism — DONE (`CoreSecurityState` LOCKED, 02.10.2026; Genesis freeze =
персистентный LIFETIME в identity row).
Atomicity/recovery — DONE 03.10.2026 (identity row + Genesis audit event в ОДНОЙ
транзакции с advisory lock audit-chain; прерывание → rollback, no half-born state).
Rerun impossibility — DONE 03.10.2026 (unique index + 409 genesis_already_performed;
development memory reset запрещён после ceremony).
