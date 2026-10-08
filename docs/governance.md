# TOLIK Governance — Privilege Broker, Capability Registry, Autonomy Policy (план, 08.10.2026)

PRE-GENESIS. Не имплементируется этим запросом. Аудит по факту кода (exp-9,
08.10.2026). Цель: фундамент, на котором TOLIK не упрётся в потолок; характер
сверху, строгая машина снизу. Компаньон: docs/computer-use.md (GUI/skills).

## 0. Аудит: что УЖЕ есть (не дублировать)

| Механизм | Где | Что сегодня |
|---|---|---|
| Identity | IdentityService, SessionPrincipal(user,device,session,role) | auth, sliding expiration, LOCKED fail-closed |
| Roles/Ceilings | AuthorizationPolicy.cs | Owner/Family/TrustedGuest/Guest, per-capability minimum-role hard Denies |
| Risk levels | ProtocolContracts.cs:41 | ReadOnly/LowRisk/Modify/Destructive/Critical |
| Escalation | AuthorizationPolicy.cs:31-54 | WriteFile на security-файлы → Critical; RunCodex → Modify+confirm |
| AuthorizationPolicy | AuthorizationPolicy.cs:92-147 | fail-closed; effective risk = max(declared, escalated); offline caps LowRisk; Modify/Destructive → confirm; Critical → confirm+MFA |
| Confirmations | ConfirmationStore.cs | bound (task,step,capability,resource) SHA-256, single-use, 10 мин, MFA для Critical; approve endpoint; Paused parking |
| Lease | AuthorizationLeaseStore | per-step, fail-closed |
| Audit | AuditWriter + hash chain | append-only, целостность, Genesis-событие атомарно |
| Watchdog | TaskWatchdog + ProactiveStore | proactive на failed/completed/stuck >30 мин, dedupe |
| Tasks | TaskLifecycle | Pending/Running/Paused/Completed/Failed/Cancelled; cancel ✓; MaxActiveTasks 100 |
| Agent capabilities | WindowsAgent | ReadFile, ListDirectory, GetProcessStatus, RunProcess, RunBuild, RunTests, RunCodex, RunOpenCode, PlaySound (+не афишированы: WriteFile, CancelTask) |
| RunProcess | WindowsAgent.cs:658-719 | alias map (калькулятор/блокнот/browser→msedge...), shell-GUI fallback, 30s timeout, kill tree |
| Filesystem guard | AgentPathGuard + TOLIK_ALLOWED_ROOTS | allowlist root'ов |
| Destructive screen | WindowsAgent.cs:803-818 | rm -rf/format/shutdown/drop... → deny для Codex/OpenCode |
| Self-healing (частично) | агент re-register backoff, watchdog, rollback-postgres.ps1, snapshot-tolik.ps1 | восстановление известными процедурами |
| Background | hosted services (backup/compaction/review/proactive/watchdog/prewarm/adaptation) | существующий каркас |
| Memory | DurableMemoryStore + PersonalContextStore | люди/юмор/личность/лор; learned rules (0.6→bump, cap 0.95) |
| Device registration | Devices + revocation | §10/§11 |
| Voice identity | Track 8 (VI-0..VI-6 done) | speaker tiers, adaptive, emotion contract |

**НАЙДЕННЫЙ SECURITY GAP (закрыть в Phase A):** `/chat` OpenApp/Build/Test,
`/audio/play`, `/voice` simple-intent пути зовут `dispatcher.ExecuteAsync`
НАПРЯМУЮ — auth + capability-advertisement только, БЕЗ AuthorizationPolicy/
lease/confirmation. Risk-классификация есть только на /tasks execute.

## 1. Capability Registry (Phase A) — ЕДИНЫЙ источник истины

Расширение существующих механизмов, НЕ новая система:

```text
CapabilityDescriptor (static registry, код = данные):
    Id                  "launch_app", "restart_service", ...
    Category            Local / System / Network / Voice / Files
    RequiredRole        из существующей модели ролей
    RiskLevel           из существующего enum
    RequiresConfirmation / RequiresMfa   (существующие флаги)
    AllowsBackground / AllowsSelfInvocation
    Reversible          (обратимость — вход в autonomy policy)
    AuditPolicy         always / on-deny-only
```

- Declared-risk map (ConfirmationStore.DeclaredRiskFor) и escalation rules
  (ToolEscalation) ПЕРЕНОСЯТСЯ в дескрипторы (один источник, дублирование
  устраняется).
- Новые capabilities получают дескриптор при регистрации агента (существующий
  BuildRegistrationJson) — несоответствиеadvertised↔declared → fail-closed.
- **Gap-fix (Phase A):** интент-пути (/chat, /voice intents, /audio/play)
  проходят через лёгкую policy-проверку (тот же Evaluate, без lease/confirm
  для ReadOnly/LowRisk; Modify+ на интент-путях → confirm через существующий
  механизм).

## 2. Autonomy Policy (Phase B) — «можно ли самостоятельно?»

Новый уровень ПОВЕРХ существующей authorization (не вместо):

```text
AutonomyDecision: AUTONOMOUS | NOTIFY | CONFIRM | MFA_CONFIRM | DENY
```

Детерминированный движок (unit-тестируемый), вход:

- capability descriptor (risk, reversible, self-invocation);
- существующий AuthorizationPolicy.Evaluate результат (роль/ceiling/locked/offline);
- контекст: кто попросил (identity/tier), канал (voice/chat/API), lease,
  часть заранее созданной задачи или нет;
- pre-authorized rules: ЯВНЫЕ правила владельца («если OBS зависнет —
  перезапусти» = structured rule в LearnedRuleStore с provenance + scope
  capability+target); blanket-правил («любые действия от админа») НЕ существует.

Базовая таблица (уточняется дескрипторами; соответствуют существующей policy):

```text
ReadOnly/LowRisk+reversible            → AUTONOMOUS (уведомление опционально)
LowRisk+not reversible                 → NOTIFY
Modify (уже с confirmation)            → CONFIRM (существующий flow)
RestartService(TOLIK-компонент)        → AUTONOMOUS/NOTIFY (self-healing §4)
RestartService(произвольная)           → CONFIRM
Firewall/security settings             → MFA_CONFIRM (Critical-дескриптор)
DeleteSystemFiles / identity / Genesis → DENY
```

Context-aware: интерактивный запрос с активной сессией Owner + LowRisk +
reversible → AUTONOMOUS; тот же запрос ночью (Night-режим voice runtime) →
NOTIFY (не беспокоить); unknown speaker → CONFIRM минимум (никогда AUTONOMOUS).

## 3. Windows Privilege Broker (Phase C) — контролируемый sudo

Проблема UAC: основной TOLIK НЕ запущен Administrator. Дизайн:

```text
TOLIK Agent (user process)
   │ Named Pipes (typed messages, impersonation-level check)
   ▼
TOLIK Privilege Broker (отдельный privileged service, admin-токен)
   │ policy: identity → capability descriptor → confirmation → autonomy
   │ audit (existing hash chain, через bridge-релей)
   ▼
Windows API / SCM / Registry (ТОЛЬКО типизированные операции)
```

Решения (не фиксировать реализацию без прототипа — Phase C0 spike):

- IPC: Named Pipes (native Windows, ACL per-pipe, identity via pipe
  impersonation — клиент = тот же user session); альтернатива: gRPC over
  localhost + OS-auth. Выбор по spike.
- Broker = ОТДЕЛЬНЫЙ процесс (admin), запускается по demand (не резидентный
  SYSTEM); communicates ТОЛЬКО типизированными capabilities — НИКОГДА
  `ExecuteAsAdmin(string command)`.
- Произвольное elevated command = отдельная capability `ElevateCommand`:
  CRITICAL-дескриптор, DENY по умолчанию, requires_confirmation+MFA+Owner,
  allowlist путей, аудит всегда. Существует в реестре, но включается явно.
- Broker НЕ резидентен, НЕ даёт shell, НЕ обслуживает несколько сессий
  одновременно (одна MFA-подтверждённая операция за раз).
- Quest/Android НЕ получают Windows privileges: брокер — локальная capability
  конкретной машины; удалённые клиенты идут через Core → PC Agent → Broker.

## 4. Self-Healing / Repair Mode (Phase D)

Только ЗАРЕГИСТРИРОВАННЫЕ recovery procedures (никогда «попробуй случайные
admin-команды»). Расширение существующего (watchdog + re-register + скрипты):

```text
RecoveryProcedure (реестр, типизированный):
    Trigger        voice_service_down / tts_down / component_dead /
                   database_unavailable / task_stuck
    Diagnosis      существующие health checks + логи
    Action         capability (RestartService / RestartSidecar / FallbackProvider)
    Authorization  autonomy policy (обычно AUTONOMOUS для своих компонентов)
    Verification   health check после действия
    Notify         ProactiveStore + RealtimeEventHub (существующие)
    MaxAttempts    2, потом STOP → explain → ask owner
```

Примеры (сценарий 6 директивы):

```text
voice service crashed → restart (брокер/известная процедура) → health → notify
TTS unavailable       → retry → fallback provider (существующая цепочка XTTS→edge→espeak)
component dead        → watchdog → controlled restart → verify
database unavailable  → diagnose → известный recovery (rollback-postgres.ps1)
                         → notify owner если не вышло
```

Repair Mode (Phase D2): capability-набор «ремонт себя» — читать свои логи,
проверять версии/конфигурацию, запускать свои тесты, перезапускать известные
компоненты, устанавливать собственные ПОДПИСАННЫЕ обновления
(InstallTolikUpdate: signed artifact + integrity + confirm), проверять
целостность (существующие integrity-механизмы). Каждая операция — через
Authorization + Audit (существующие).

## 5. Background Tasks + Proactive (Phase E)

Расширение существующей TaskLifecycle (НЕ новая система):

- **TaskSteps**: таблица шагов (сейчас steps живут только в plan одного
  execute) — долгие задачи получают persist-шаги с per-step статусами.
- **Pause/Resume API**: POST /tasks/{id}/pause | /resume + переход
  Paused→Running (сейчас Paused — только парковка confirmation, выхода нет).
- **Authorization snapshot**: задача при создании фиксирует (capability, target,
  lease, ceiling); при resume/продолжении — РЕВАЛИДАЦИЯ через существующие
  lease/policy (старое разрешение не растёт с возрастом; owner req 14).
- **Triggers**: schedule (существующие hosted-задачи) + condition («если X»)
  + event-driven; каждое срабатывание проходит autonomy policy.
- **PAUSED_BY_RESTART**: после рестарта Core задача с Persist=true
  восстанавливается в Paused; resume — по policy (owner req 29).

Notifications (Phase E2): Reactive vs Proactive разделение (существующий
ProactiveStore/watchdog): proactive — AUTONOMOUS/NOTIFY результаты self-healing,
напоминания, статусы; «я решил изменить конфигурацию» без разрешения — никогда.
Rate-limit + приоритеты (существующие).

## 6. Personality / Relationship Layer (Phase F)

Существующее: PersonalContextStore (личность/юмор/лор/отношения), learned rules.
Разделение (треб. #16/#17 — КРИТИЧНО):

```text
Personality ≠ Authorization
```

- Personality управляет КАК сказано (дружески/юмор/подкол/спор); Security
  решает ЧТО можно. DENY-ответ проговаривается ЧЕРЕЗ личность
  («Не, это за пределами моих полномочий, и это не обсуждается 😄»), но
  решение — детерминированное, личность его не обходит.
- Human-like коммуникация вместо корпоративной: подтверждения/уведомления
  формулирует personality layer («Ща, перезапущу. OBS опять решил умереть»),
  технический execution — строгий policy (треб. #19: человеческий
  human-in-the-loop вместо «CONFIRM ACTION ID 84729»).
- Внутреннее состояние (Mood/Energy/Attention/Confidence/Activity) —
  безопасное представление для Cephalon (существующий VoiceRuntimeState +
  emotion contract VI-6); НЕ управляет Security: Emotion ≠ permissions в
  обе стороны (злость не повышает, радость не снижает).

## 7. Human-in-the-loop Confirmation UX (Phase F2)

Существующий bound-confirmation механизм (SHA-256, single-use, 10 мин) —
НЕ дублируется. Обёртка: человеческая формулировка:

```text
TOLIK голосом/чатом: «Нужно перезапустить службу X. Секунды три голосовой
режим будет недоступен. Перезапускаю?»
→ «Да» → bound confirmation (identity+action+target+params+risk+window
  из существующего механизма) → consume при execute → audit
```

«Да» НИКОГДА не универсальное разрешение (треб. #20) — связь через
существующий ConfirmationStore.

## 8. Emergency Stop / Kill Switch (Phase G)

```text
STOP TOLIK (Owner, voice/chat/API)
→ активные autonomous/background tasks → PAUSED (existing CTS + lifecycle)
→ новые privileged operations → DENY (autonomy policy — глобальный тумблер)
→ identity / memory / Genesis / LIFETIME — НЕ затронуты
→ восстановление: Owner authorization (существующая модель)
```

Реализация: расширение существующих (AutonomyPolicy toggle + TaskLifecycle
pause-all); СОФТ-стоп — в отличие от LOCKED (который ревокует сессии и
наступает при tamper). LOCKED остаётся для security-инцидентов; STOP —
для управляемой паузы автономии.

## 9. File/Network/Secret Capability Boundaries (Phase H)

**Files**: существующие TOLIK_ALLOWED_ROOTS + AgentPathGuard расширяются
scopes (TOLIK data / documents / dev workspace / selected folders / system
areas) + per-op permissions (READ/WRITE/MOVE/DELETE/EXECUTE отдельные
descriptors): «посмотреть» ≠ «удалить». Системные области — DENY по умолчанию.

**Network**: NetworkCapability (destination/protocol/action/scope/rate/
authorization) — destinations allowlist, rate-limit, credential БЕЗ выдачи
LLM. Удалённые клиенты — существующая модель (device/session/revocation).

**Secrets**: Secret Access Layer — LLM не видит секреты принципиально:
```text
TOLIK needs credential → capability → Secret Manager (OS-protected) → op
```
Секреты приходят к исполнителю операции, не в контекст модели (существующие
правила §6/§12/§38 + secrets.js denylist — расширить на capability-уровень).

## 10. PC State Model (Phase H2) → docs/computer-use.md §4

Единое представление (PC State / World Model) — см. компаньон-план.

## 11. Порядок, зависимости, решения

```text
Phase A Capability Registry + gap-fix интент-путей   DONE 09.10.2026 (commits
    a352f68 + registry tests; GuardIntentCapability на всех 5 интент-путях;
    ConfirmationStore.DeclaredRiskFor → реестр; /tasks execute не тронут)
Phase B Autonomy Policy                              DONE 09.10.2026 (commit
    affc51b; AutonomyPolicy.Decide детерминированный; wire-in audit-only
    на /tasks execute; enforcement Notify/Confirm — E2/F)
Phase C Privilege Broker (C0 spike IPC)              ПОСЛЕ Genesis (spike до)
Phase D Self-Healing / Repair                        [A, B есть; D2 после Genesis]
Phase E Background Tasks v2                          E-min DONE 09.10.2026
    (commit affc51b: TaskLifecycle.TryResume Paused→Running, /pause /resume
    API, DB-синк, аудит; TaskSteps/PAUSED_BY_RESTART — E2 после Genesis)
Phase F Personality/Human-in-the-loop UX             [B есть; F после Genesis]
Phase G Emergency Stop                               [B есть; G после Genesis]
Phase H File/Network/Secret boundaries               file scopes — УЖЕ ЕСТЬ
    (AgentPathGuard + TOLIK_ALLOWED_ROOTS); network/secret — после Genesis
Phase I+ — Computer Use layers                       [docs/computer-use.md,
    после Genesis]
```

Решения ДО Genesis: A (реестр + gap-fix), B (autonomy — уже проектируется
существующей policy), E-минимум (pause/resume + auth snapshot); H-минимум
(file scopes — уже есть). Решения ПОСЛЕ Genesis: C (брокер — spike до, код
после), D2 (repair full), E2 (proactive full), F (relationship full).

Security risks: брокер = новая privileged поверхность (минимизировать:
типизированные ops, non-resident, audit); autonomy ошибки (fail-closed,
DENY-дефолты); personality bypass (разделение тестами); prompt injection
через UI (см. computer-use.md §6).
