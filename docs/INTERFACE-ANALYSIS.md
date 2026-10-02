# Анализ `interface/` — браузерная reference-реализация протокола TOLIK

## Что это на самом деле

Не просто «наработка интерфейса» — это **работающий прототип всей архитектуры TOLIK в браузере**:

| Файл | Что реализует |
|------|--------------|
| `contracts.js` (19 КБ) | Протокол v1: envelope (`protocolVersion/correlationId/messageType/sentAtUtc/payload`), strict-JSON схемы ВСЕХ сообщений, **ESCALATION_RULES** (WriteFile в security-config → Destructive/Critical + подтверждение; RunCodex → Modify + подтверждение), **запрет command-surface** (никаких `command/shell/exec/argv` полей) |
| `transport.js` (6 КБ) | Подписанные фреймы (JWS-подобные), **nonce-store** (replay-защита), pinned peer keys |
| `core.js` (37 КБ) | Полный Core: **authorization leases** (одноразовые consumeAuthorization на каждый шаг!), **confirmation binding** (bindingHash привязывает подтверждение к конкретной задаче+шагу+аргументам), revocation freshness (окно 15 мин, offline-lease только read-only), **LOCKED state machine**, роли Guest/TrustedGuest/Family/Owner с risk-ceilings |
| `identity.js` | IdentityStore: устройства, сессии, enrollment tokens, leases; IndexedDB/Memory backing |
| `policy.js` | AuthorizationEngine — детерминированная оценка с decision trace |
| `agent.js` + `vfs.js` | In-browser Windows Agent + виртуальная ФС с allowlist-изоляцией |
| `workspace.js` | C#-проект внутри VFS (SUEK KIP parser) — песочница для demo-задач |
| `dotnet.js` | **Симулятор MSBuild/dotnet test** — строит диагностику по реальному C#-коду в VFS |
| `planner.js` / `model.js` / `tasks.js` / `audit.js` / `secrets.js` | Planner, model-router (стриминг), TaskManager, аудит, секрет-сканер |

README описывает два UI-режима (Chat + Cephalon-глаз) — HTML-оболочки в папке нет, только ES-модули.

## Чем это ЦЕННО относительно текущего C#

Реализованные в JS механизмы, которых НЕТ в C# (или есть в упрощённом виде):

1. **Leases** — C# проверяет capability на каждый вызов, но не имеет временных lease с одноразовым `consumeAuthorization` (§8: "permissions must be evaluated when a tool is actually executed" — в JS это формализовано строже)
2. **Confirmation binding** — подтверждение привязано к хэшу (taskId+stepId+capability+args), нельзя переиспользовать для другого шага
3. **Escalation rules** — запись в security-critical исходники = Critical; у C# AuthorizationPolicy этого нет
4. **Replay-защита транспорта** — nonce + подпись каждого фрейма
5. **LOCKED state + degraded offline leases** — при устаревшем revocation-стейте выдаются только read-only capabilities
6. **Запрет command-surface на уровне схемы** — `FORBIDDEN_SURFACE_KEYS` прямо в валидации протокола

Известные расхождения (README): Core пинит `agent:<kid>`, транспорт подписывает raw kid; Core хранит `bindingHash`, policy ищет `boundTo` — в JS-прототипе обойдено. Это места, где C# и JS-прототип расходятся — **синхронизировать при внедрении**.

## Что с ним делать — 4 опции

### Опция A. Спецификация протокола (рекомендую, ~5–8 дней)
Принять `interface/` за эталон протокола и довести C# до него:
- `TOLIK.Contracts`: envelope + strict schemas + escalation rules + forbidden surface (перевод contracts.js → C#)
- `AuthorizationPolicy`: leases + одноразовые authorization + binding-подтверждения
- LOCKED state в Core.Host
Это самая большая ценность: JS уже содержит **проработанную security-модель**, которую AGENTS.md требует, но C# ещё не реализовал. Часть её проверяема без внешних систем.

### Опция B. Реальный WebUI (средний приоритет, ~3–5 дней)
Встроить в `wwwroot/` как статические ES-модули, заменив локальный `TolikCore` на HTTP-клиент к C# Core (те же contracts/schemas уже на клиенте). Chat + Cephalon-режимы готовы концептуально; HTML-оболочку придётся восстановить (в папке её нет).

### Опция C. Test oracle (быстро, ~1–2 дня)
Запускать strict-JSON/contracts валидаторы как Node-тесты против C# API: отправлять заведомо невалидные envelope'ы и требовать 400 + правильный код ошибки. Дешёвая страховка регрессий протокола.

### Опция D. Песочница протокола (0 усилий)
Оставить как playground для эволюции протокола до переноса в C#.

## Рекомендация

1. **Сейчас (этап MVP)** — Опция C: подключить contracts-валидацию как smoke Node-скрипт + зафиксировать расхождения из README в техдолг.
2. **Сразу после MVP** — Опция A поэтапно: (a) escalation rules в C# AuthorizationPolicy, (b) confirmation binding, (c) leases, (d) transport-подписи. Каждый шаг — тесты.
3. **UI** — Опция B после стабилизации A, чтобы UI двигал реальный протокол, а не симулятор.

## Прямые TODO из прототипа в backlog C#

- [ ] `ESCALATION_RULES` → `AuthorizationPolicy` (security-config → Destructive, security-critical-source → Critical, RunCodex → Modify+confirm)
- [x] Confirmation record с `bindingHash`, одноразовое потребление — IMPLEMENTED: `ConfirmationStore` (src/TOLIK.Infrastructure/Authorization/ConfirmationStore.cs), canonical SHA-256 binding Hash(taskId,step,capability,resource), Approve/Consume split with Consumed flag, consumed fresh per dispatch step (Program.cs DispatchGuardedAsync), 02.10.2026.
- [x] Lease: TTL 60s, offline-режим = read-only capabilities — IMPLEMENTED: `AuthorizationLeaseStore` (src/TOLIK.Infrastructure/Authorization/AuthorizationLeaseStore.cs), in-memory TTL 60s default, issue/validate/revoke fail closed (unknown/revoked/expired authorize nothing), single-use ConsumeAuthorization; wired into /tasks/{id}/execute DispatchGuardedAsync (lease issued per run bound to session, validated per step, expiry feeds AuthorizationExpiresAt); offline cap enforced in AuthorizationPolicy via OfflineMode flag — effective risk above the read-only band (ReadOnly/LowRisk non-modifying per AGENTS.md §11) is a hard Deny confirmation cannot rescue; per-step consumption stays delegated to the ConfirmationStore binding (single source, avoids double-consumption breaking confirmation retries), 02.10.2026.
- [ ] LOCKED state в Core.Host (2 известные расхождения из README синхронизировать)
- [x] Envelope-валидация: unknown fields → 400, strict canonical JSON — IMPLEMENTED: global UnmappedMemberHandling.Disallow in ConfigureHttpJsonOptions (Program.cs), unknown fields auto-400 on all POST DTO bindings, producer pre-check clean, 2 tests, 02.10.2026.
- [x] Roles: risk ceilings в AuthorizationPolicy — IMPLEMENTED: `TolikRoles` (src/TOLIK.Infrastructure/Identity/TolikRoles.cs), typed parse of the existing Guest/AuthorizedUser/Owner roles (unknown/variant names fail closed), ceilings Owner→Critical, AuthorizedUser→Destructive, Guest→deny; enforced on effective (post-escalation) risk, hard Deny, not rescuable by confirmation/MFA, 02.10.2026. Family/TrustedGuest roles deferred — no enrollment path yet.
- [x] Запрет command-surface (`FORBIDDEN_SURFACE_KEYS`) — IMPLEMENTED: C# enforcement via CommandArgumentPolicy denylist (ProtocolContracts.Validate + AgentTransportAuthorization.Evaluate Deny), 2 tests, 01.10.2026.
