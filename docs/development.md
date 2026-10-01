# TOLIK Development Mode

What is allowed while the lifecycle state is DEVELOPMENT (see `lifecycle.md`).

## Allowed

- Test memory, fake users, mock identity, test credentials.
- `TOLIK_DEV_AUTO_TOKEN=true` (same-machine UI convenience only).
- Destructive reset: `DELETE /memory`, DB wipe/re-migrate, `docker compose down -v`.
- Integration tests (create `tolik_test_*` databases, mock enrollments).
- Migrations, experiments, architecture changes.

## Development credentials inventory (must never leak into LIFETIME)

| Credential | Where | LIFETIME rule |
|---|---|---|
| `TOLIK_DEV_AUTO_TOKEN` | process env (launcher) | must be absent; preflight fails if set |
| `TOLIK_BOOTSTRAP_CODE` | `.env` (gitignored) | rotate after all enrollments; revoke consumed codes |
| Test sessions/tokens | `tolik_test_*` databases | test DBs only, never production |
| `~/.tolik/session-token` | owner workstation | per-device, `chmod 600`, revocable |

## Test data

- Fixtures: `tests/fixtures/` (incl. `truncated.backup` for restore-safety tests).
- Voice dataset: `artifacts/`, `datasets/`, `voice_reference/` (gitignored, local only).
- Session evidence: `.omo/evidence/` (gitignored as of baseline).

## Rules

1. Everything development is labeled (test DB names, `user_feedback`/test sources).
2. No dev credential or test memory may become part of TOLIK's personality.
3. Before Genesis: rotate bootstrap, revoke test sessions, verify no `origin=development` rows in production tables.
