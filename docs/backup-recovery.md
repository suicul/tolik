# TOLIK Backup & Recovery

## What exists

- `scripts/backup-postgres.ps1 -BackupPath <file>` — `pg_dump` custom format,
  integrity-checked via `pg_restore --list`, SHA-256 sidecar. Manually verified.
- `scripts/restore-postgres.ps1` + `scripts/test-restore-safety.ps1`
  (restore guard + `tests/fixtures/truncated.backup` fixture).
- Docker volume `tolik_postgres_data` persists across `compose down`.

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
