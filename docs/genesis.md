# TOLIK Genesis

One-time birth ceremony of a permanent instance. Status: **concept defined,
not implemented** (no Genesis code exists as of the baseline commit).

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

Preflight checks, instance identity table, owner binding ceremony step,
freeze/lock mechanism, atomicity/recovery, rerun impossibility.
