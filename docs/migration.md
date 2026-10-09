# TOLIK Core Migration — операционная процедура (docs/identity-presence.md §3-§4, IP-2)

Перенос места проживания Core (PC→VPS→PC) без потери личности. Одна Identity —
ONE ACTIVE CORE: split-brain предотвращается технически (Core Lease/Epoch/Fencing).

## Протокол миграции

`scripts/migrate-core.ps1` — транзакционный, abort-on-fail на каждом шаге,
`-WhatIfMode` = только план:

| Шаг | Действие | Механизм |
|---|---|---|
| 1 PREPARE | source /health: coreLease (epoch/host) | существующий /health |
| 2 VERIFY DESTINATION | compose/docker/параметры | docker CLI |
| 3 PAUSE SOURCE (опц.) | честное предупреждение: pause-all endpoint отсутствует — активные задачи приостанови через /tasks API вручную | без фейка |
| 4 SNAPSHOT | snapshot-tolik.ps1 (code+docs+dump+manifest) | существующий |
| 5 SYNC | snapshot → destination host (локально copy; удалённо scp/robocopy — параметризируется) | — |
| 6 INTEGRITY | SHA256 dump vs sidecar | существующий паттерн |
| 7 BOOT | compose build+up (DEV_AUTO_TOKEN off!) | docker compose |
| 8 HEALTH | poll /health до healthy (120s bound) | — |
| 9 EPOCH+FENCE | destination startup fences expired/absent; ждём epoch > source; **CONFLICT (source ACTIVE) → abort: «останови старый core первым»** — скрипт не фенсит принудительно | CoreLeaseService (IP-1) |
| 10 VERIFY | компоненты + epoch comparison | /health |
| 11 FINALIZE | сводка: old epoch→STALE, new epoch, snapshot; напоминание остановить старый процесс | — |

Старый core становится **STALE через renewal loop** (fenced_by_newer_runtime →
перестаёт действовать сам) — скрипт никогда не убивает процессы.

## Preconditions

- Lease дизайн (IP-1): старый core фенсится/истекает; destination отказывает CONFLICT.
- `TOLIK_DEV_AUTO_TOKEN` **выключен** на destination (не auto-mint owner sessions).
- Backup/restore: docs/backup-recovery.md (snapshot validated, SHA256 sidecar).
- Postgres: docs/rollback-postgres.ps1 (Docker-verified) — rollback путь.

## Recovery / Resurrection (identity-presence.md §4)

Потерян/уничтожен/провайдер умер → новый host восстанавливает из последнего
валидного состояния:

- last valid snapshot + append-only journal + task checkpoints;
- identity integrity (существующие механизмы);
- **recovered host ОБЯЗАН acquire НОВЫЙ epoch — никогда не воскрешается со
  старым** (split-brain prevention);
- recovery authorization (существующая модель).

## Migration history

Аудит-события на chain tip (существующий hash chain): `core.lease.acquired` /
`core.lease.fenced` / `core.lease.stale_detected` — epoch/host/runtime ids only
(без секретов). Миграция остаётся deliberate milestone: техническая готовность
сначала, операция — с владельцем.
