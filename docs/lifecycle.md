# TOLIK Lifecycle

Canonical state model. Exactly one state is active at a time.

```text
DEVELOPMENT → PRE-GENESIS → READY_FOR_GENESIS → GENESIS → LIFETIME
```

`PRE-GENESIS` is hardening work, not a runtime mode. `GENESIS` is a one-time
ceremony (see `genesis.md`). There is no code gate yet — the states below are
normative until the lifecycle subsystem lands (Phase 4).

## DEVELOPMENT (current)

Allowed: test memory, fake users, test credentials, dev tokens
(`TOLIK_DEV_AUTO_TOKEN`), destructive reset, integration tests with mock
identity and test data, migrations, experiments, architecture changes.

Required: everything development must be labeled as such (test DB names,
`user_feedback`/test sources, dev-only flags default off). Dev credentials
must never become part of TOLIK's personality or permanent memory.

## PRE-GENESIS

Hardening only: no new user features. Output is `READY_FOR_GENESIS` when the
checklist in the master directive §24 is green.

## READY_FOR_GENESIS

Frozen, verified, documented. No code or doc changes except Genesis ceremony
prep. Genesis is NOT run automatically from this state.

## GENESIS

One-time ceremony, owner-present. Either completes atomically (→ LIFETIME)
or rolls back with no half-born state. See `genesis.md`.

## LIFETIME

The born instance. Forbidden: memory reset, owner replacement, re-bootstrap,
silent history deletion, audit disable, dev credentials. All admin operations
go through the security workflow (MFA + audit). See `genesis.md` (After Genesis).

## Per-state matrix

| | DEVELOPMENT | READY_FOR_GENESIS | GENESIS | LIFETIME |
|---|---|---|---|---|
| Memory reset | allowed | forbidden (frozen) | only via ceremony | forbidden |
| Bootstrap | any code | frozen code | one-time | forbidden |
| Dev tokens | allowed (flagged) | present but off | BLOCKED if present | forbidden |
| Test identity | allowed | absent | owner only | owner only |
| Migrations | allowed | frozen | forbidden | forbidden |
| Audit | on (best effort) | on, verified | on, ceremony logged | on, append-only |
| Experiments | allowed | forbidden | forbidden | forbidden |
