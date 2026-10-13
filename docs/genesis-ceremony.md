# TOLIK Genesis — церемония: операционный чеклист

Runbook владельца. Дизайн и инварианты — `docs/genesis.md`; здесь только
ПОРЯДОК ДЕЙСТВИЙ с точными командами. Всё проверено по коду (Program.cs,
GenesisService, GenesisSeed, GenesisVoiceEnrollment) на 14.10.2026.

## 0. Что делает церемония

```text
Preflight (все гейты) → identity row + Genesis audit event В ОДНОЙ транзакции
→ LIFETIME. Повторный запуск невозможен (409 genesis_already_performed).
Прерывание → откат транзакции, half-born состояния не бывает.
```

После церемонии запрещены без security-workflow: сброс памяти, замена
владельца, re-bootstrap, тихое удаление истории. Повторный seed ума после
активации ОТВЕРГАЕТСЯ (continuity, mind.md §7).

## 1. Предварительные условия (до дня церемонии)

| # | Действие | Проверка |
|---|---|---|
| 1.1 | Бэкап валидирован | `backups/tolik-backup-pre-genesis-fresh.dump` + SHA-256 (`9e1df711…`), снят **14.10.2026**; restore-тест пройден в тот же день: дамп восстановлен в отдельную scratch-БД (15 таблиц, pg_restore exit 0), scratch удалена. Скрипт: `.\scripts\backup-postgres.ps1` (требует `pwsh -ExecutionPolicy Bypass -File`) |
| 1.2 | Тесты зелёные | `dotnet test TOLIK.sln` — 0 провалов (авторизационная матрица — часть preflight-доверия) |
| 1.3 | Память чиста от development | 0 строк `origin=development` вне тест-фикстур (проверено 03.10.2026; после — не запускать dev-сессии в production DB) |
| 1.4 | MFA-устройство готово | приложение-аутентификатор установлено |

## 2. День церемонии — по шагам

Все команды из корня `C:\DEV\TOLIK_QWEN`. Токен сессии: через
`TOLIK_SESSION_TOKEN` env — НИКОГДА в командной строке (аудит m6).

### Шаг 1 — Выключить dev-токены (навсегда для этой установки)

```powershell
# .env / compose environment: TOLIK_DEV_AUTO_TOKEN удалить или = false.
# ВАЖНО: флаг дрейфует — любой `docker compose up` без явного env сбросит
# его. Проверить фактическое состояние в контейнере:
docker compose exec core printenv TOLIK_DEV_AUTO_TOKEN
# ожидание: пусто / false. Если true — поправить env и:
docker compose up -d core
docker compose exec core printenv TOLIK_DEV_AUTO_TOKEN   # контроль: off
```

Preflight-гейт `dev_tokens_enabled` читает конфиг Core — гасить в нём, не
только в шелле. Начиная с этого шага dev-автотокен не включать обратно.

### Шаг 2 — Стек жив и кор здоров

```powershell
(Invoke-WebRequest 'http://127.0.0.1:5080/health' -UseBasicParsing).Content
# core/voice/memory: true; locked: false; emergencyStopped: false
```

`/auth/local-token` работает ТОЛЬКО пока dev-токены on — после Шага 1 сессию
владельца получать через обычную аутентификацию устройства (bootstrap-токен
в `~\.tolik\session-token` сохранён с первичной enrolment).

### Шаг 3 — Аутентификация владельца + MFA

```powershell
# 3a. Enroll MFA (однократно, если ещё не сделано):
POST /mfa/enroll   → { deviceId, secret }   # секрет в аутентификатор
# 3b. Контроль кода:
POST /mfa/verify   { deviceId, code }       # Success: true
```

### Шаг 4 — Seed ума: загрузка ДО церемонии (данные), активация — на церемонии

Seed-данные (темперамент ≤256 символов + 1–12 базовых ценностей ≤128) грузятся
pre-ceremony; ум читает их ТОЛЬКО после активации (`SeedStatus`:
NotSeeded → Seeded → Activated). Контроль статуса: `GET /mind/state` →
`"status":"Seeded"` перед церемонией.

### Шаг 5 — Голос владельца (Phase 2 церемонии)

```text
POST /genesis/voice/sample   × 5   # фиксированные RU-фразы (шипящие/свистящие/
                                   # глухие/звонкие/р-л); сырой звук НЕ сохраняется
POST /genesis/voice/complete       # ≥ 4 из 5 → канонический профиль
```

Неудачная фраза повторяется индивидуально; < 4 из 5 — церемония остаётся в
`voice_enrollment_pending`, набор перезапускается (`/genesis/voice/reset`).

### Шаг 6 — САМА ЦЕРЕМОНИЯ (необратимо)

```text
POST /genesis/ceremony
{
  "codeIdentity":      "<git commit хэш базовой версии>",
  "knowledgeIdentity": "<хэш начального набора знаний>",
  "mfaDeviceId":       "<id из Шага 3>",
  "mfaCode":           "<текущий код>",
  "backupConfirmed":   true,     ← заверяете ЛИЧНО: бэкап есть и валиден
  "restoreTested":     true      ← заверяете ЛИЧНО: восстановление проверено
}
```

Ответ: `{ instanceId, bornAt, genesisVersion, codeIdentity,
knowledgeIdentity, lifecycleState: "LIFETIME", ownerIdentity }`.

**Ложное заверение бэкапов = единственный незащищённый путь** — гейт доверяет
аттестации владельца. Прежде чем ставить `true` — убедиться, что Шаг 1.1
фактически выполнен СЕГОДНЯ.

### Шаг 7 — Верификация рождения

```text
GET /genesis/identity   → identity row, LIFETIME           (owner-only)
GET /mind/state         → "status":"Activated"; ум читает seed
GET /mind/journal       → целостная операционная история
```

Повторный `POST /genesis/ceremony` → 409 `genesis_already_performed` — так и
должно быть (rerun impossibility). Проверить один раз и больше не трогать.

## 3. Если что-то пошло не так

| Симптом | Значение | Действие |
|---|---|---|
| 400 `preflight_failed` + список гейтов | не пройдены перечисленные гейты | исправить только перечисленное; аудит-событие DENY уже записано (нормально) |
| 409 `genesis_already_performed` | рождение уже случилось | НЕ пытаться обойти; работать с `GET /genesis/identity` |
| 503 `genesis_unavailable` | прерывание/сбой БД | транзакция откатилась сама; устранить причину; повторить Шаг 6 |
| Прерван голосовой набор | `voice_enrollment_pending` | identity НЕ рождена; `/genesis/voice/reset` и заново |

## 4. После церемонии

- Dev-эндпоинты/dev-токены закрыты навсегда; аварийный доступ — только через
  security-workflow (`authorization.md`, `security.md`).
- Ум переходит к mind-driven expression (seed активен → LIFETIME).
- Открываются post-Genesis треки: SE-5/6 (self-development), IP-5/7/8
  (presence/VPS), MM-4..6 (vision), Android-клиент.
