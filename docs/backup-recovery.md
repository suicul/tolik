# TOLIK Backup & Recovery

## What exists

- `scripts/backup-postgres.ps1 -BackupPath <file>` — `pg_dump` custom format,
  integrity-checked via `pg_restore --list`, SHA-256 sidecar. Manually verified.
- `scripts/restore-postgres.ps1` + `scripts/test-restore-safety.ps1`
  (restore guard + `tests/fixtures/truncated.backup` fixture).
- Docker volume `tolik_postgres_data` persists across `compose down`.
- **Pre-genesis backup (03.10.2026): `backups/tolik-backup-pre-genesis.dump`**
  (persistent local location, SHA-256 `af9f1900…858`). Restore-safety test PASS:
  fresh target restored with migrations + sentinel, corrupt archive rejected,
  existing target rejected (guard rails verified). Genesis requirement (backup
  exists + restore tested + data survives): **exists + tested — demonstrated;
  off-machine copy — owner's action.** Dumps are gitignored — plaintext secrets.

## What does NOT exist (gaps)

- No schedule (manual runs only).
- No tested full DR run (fresh machine → restore → boot → verify).
- No code/weights/docs snapshot discipline (only DB is covered).
- No rollback for migrations or agent actions.

## Procedure (manual, until scheduled)

1. `backup-postgres.ps1` → verify SHA-256 → copy dump + `.sha256` off-machine.
2. For restore: `test-restore-safety.ps1` first, then `restore-postgres.ps1`.
3. Genesis requirement (§22): backup exists + restore tested + important data
   survives — all three must be demonstrated before READY_FOR_GENESIS.

## Retention

Keep last 7 daily dumps minimum; encrypt before off-machine storage
(pending gap C7 — until then, treat dumps as plaintext secrets).
