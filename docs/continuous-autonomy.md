# TOLIK Continuous Autonomy — LongRunningTask + TaskSupervisor (план, 09.10.2026)

PRE-GENESIS. Не имплементируется этим запросом. Интегрируется в существующие
системы. Компаньоны: docs/mind.md (автономная когниция), docs/governance.md §5
(leases/pause-resume/auth snapshot), docs/self-evolution.md, docs/genesis-upbringing.md.

## 0. Аудит: что УЖЕ есть (не дублировать)

| Существующее | Отношение к PART I |
|---|---|
| TaskLifecycle (Pending/Running/Paused/Completed/Failed/Cancelled, CTS, events, MaxActiveTasks) | LongRunningTask = РАСШИРЕНИЕ lifecycle (новые состояния), не новая система |
| Pause/Resume API + Paused→Running (E-min ✓) | База для WAITING/RECOVERING |
| AuthorizationLeaseStore (per-step, fail-closed) | TaskAuthorizationLease = РАСШИРЕНИЕ (duration/environments/forbidden) |
| DispatchGuardedAsync (risk escalation + lease + confirm + policy) | Каждая ступень долгой задачи — тот же guard |
| TaskWatchdog + ProactiveStore | Stall detection + уведомления — расширение |
| TaskSteps (план E2) | Checkpoints/journal опираются |
| RealtimeEventHub (per-user events) | UserDisconnected/TaskReturned семантика |
| Mind (Track 11 план): autonomous cognition/activity selection/attention | Continuous autonomy engine = Mind + TaskSupervisor |
| M8 game detection + Night/Offline + бюджеты (SE-3) | Resource budgeting |
| Vision/UI observation (CU-1/CU-2, pre-Genesis допустимо) | Внешняя верификация состояния |

## 1. LongRunningTask — first-class persistent (треб. 1-4, 15)

Задача = активная persistent цель, НЕ «выполнение команды»:

```text
«Доведи проект Грильяжа до конца» → objective/project/expected result/
constraints/resources/required capabilities/completion criteria →
LongRunningTask (persisted, принадлежит TOLIK/Core — НЕ voice-сессии)
```

**Расширенный lifecycle** (интеграция с существующим TaskLifecycle):

```text
CREATED → PLANNED → AUTHORIZED → RUNNING → PAUSED → WAITING → RECOVERING
→ COMPLETED → VERIFIED
терминальные: CANCELLED | FAILED | BLOCKED | ABANDONED
```

Существующие состояния маппятся (Running=Running, Paused=Paused...); новые:
PLANNED (план создан, ожидает auth), AUTHORIZED, WAITING (активное ожидание —
§4), RECOVERING (восстановление), VERIFIED (проверено после completion),
BLOCKED/ABANDONED.

**Task Scope** (треб. #4) — при широкой задаче создаётся ЯВНО:

```text
project / objective / known constraints / allowed capabilities /
allowed files (AgentPathGuard scopes) / allowed environments /
allowed network / max resource budget / max duration /
allowed autonomous actions / actions requiring confirmation /
completion criteria / stop conditions
```

НИКАКОГО unlimited authority. «Do what is necessary» (треб. #5): fix project
code — разрешено scope'ом; НЕ расширяется молча: удаление посторонних файлов,
firewall, случайный софт, экспонирование сервисов, сообщения, деньги, security —
вне scope → authorization boundaries остаются authoritative.

**Ownership** (треб. #15): Owner/Creator/AuthorizationContext (точный контекст
создания: capability/target/lease/ceiling)/CreationTime/Priority/Scope/
Expiration/AutonomyLevel. Не переинтерпретировать позже по personality/memory.

## 2. Authorization Lease для долгих задач (треб. #16)

Расширение существующего AuthorizationLeaseStore:

```text
TaskAuthorizationLease: allowed capabilities / allowed project /
allowed duration (истечение → normal autonomy) / allowed environments /
max resource usage / forbidden operations (explicit denylist)
```

«Поработай над проектом, пока меня нет» → lease с expiry; по истечении —
возврат к обычной autonomy (не бесконечное разрешение). per-step lease
валидация (существующая) остаётся.

## 3. TaskSupervisor (треб. #17-#19)

```text
TaskSupervisor (BackgroundService — паттерн существующих hosted services):
   monitor long-running tasks (heartbeat/liveness)
   recover workers (RECOVERING)
   checkpoint (§4)
   detect stalls (§3.1)
   enforce budgets (resource/time/MaxAttempts)
   enforce authorization (lease/scope — НЕ bypass)
   detect completion → trigger verification (VERIFIED)
   notify user (ProactiveStore + RealtimeEventHub)
```

**Heartbeat/liveness** (треб. #18): last activity/last progress/worker health/
current operation/expected next event → различает working vs stuck. Без LLM
вызовов — event/timer driven.

**Stall detection** (треб. #19): same state 30 мин → оценить: ожидаемо долгая
операция? приложение ждёт? сеть? deadlock? crash? ресурсы? → recover/wait/
replan/notify. НЕ сообщать failure преждевременно. Существующий watchdog
(>30 мин stuck) — расширение семантикой.

## 4. Checkpoints + Resume + Journal (треб. #13, #14, #9)

**Checkpoint** (треб. #13): current state/progress/files modified/active branch/
environment/dependencies/last action/next action/rollback information.
Crash → restore checkpoint → verify current external state → resume safely.
**Resume after restart** (треб. #14): reconnect → validate environment → check
repo/branch/modified files/build state/last checkpoint → determine: previous
action completed? → resume или rollback. НИКОГДА не считать действие успешным
потому что оно началось (§50 no fake — mind.md).

**Task journal** (треб. #9 — паттерн mind.md journal §11):

```text
14:21 Build failed | 14:23 inspected compiler output | 14:31 identified API
mismatch | 14:40 implemented compatibility layer | 14:42 unit tests passed
```

Timestamp/Event/Action/Result/Reason/Evidence/Next step → позже естественный
разговор («что ты делал?» — из реального journal). Persist: в существующей
memory (journal source) + TaskSteps.

## 5. Real progress + Absence/Return (треб. #3, #6-#8)

**Progress** (треб. #8): OverallProgress/Phase/CompletedSteps/CurrentStep/
BlockedSteps/FailedAttempts/LastSuccessfulAction/CurrentHypothesis/
RemainingWork/EstimatedRemaining/Confidence — из фактических свидетельств,
никогда не выдумывается.

**UserDisconnected ≠ TaskCancelled** (треб. #6 — КРИТИЧНО): отсутствие
владельца НЕ паузит задачу (если policy не говорит иного); раздельные события
(существующие hub/lifecycle); останавливают только: явная отмена/stop
condition/authorization expiration/safety event/resource constraint/failure.

**User return** (треб. #7): «С возвращением. По Грильяжу 87%: билд починил,
интеграционные прошли, осталась упаковка» / «Не закончил: 73%, упёрся в X,
два варианта откатил» — из реального progress/journal, через personality
(governance §6), без fabrication.

## 6. WAITING + Parallel + Recovery (треб. #10-#12)

**WAITING — активное состояние** (треб. #11): IDLE ≠ WAITING (download/
compilation/timer/user input/app startup/external service/reconnection).
Пока ждёт: мониторит/выполняет незвязанные авторизованные подзадачи/
maintains state/recovers. Без LLM — события/таймеры (существующий паттерн).

**Autonomous recovery** (треб. #10): OBSERVE → CLASSIFY → RETRY (один раз
same action) → DIAGNOSE → REPLAN → ACT → VERIFY. Bounded retries; НИКОГДА
build→fail→build→fail→вечно.

**Parallel work** (треб. #12): независимые подзадачи параллельно при
(safe/resources/no conflicting writes/dependencies) — существующий паттерн
(параллельные fixer-лейны/fixer instances); resource budgets (SE-3).

## 7. Continuous Autonomy Engine (треб. #20, #21, #22)

Mind (Track 11) + TaskSupervisor = continuous scheduler:

```text
WORLD EVENT + INTERNAL STATE + ACTIVE GOALS + BACKGROUND TASKS
+ RESOURCE STATE → MIND EVALUATION → CANDIDATE ACTIVITIES → PRIORITY
→ AUTONOMY POLICY → AUTHORIZATION → TASK SUPERVISOR → EXECUTION
→ OBSERVATION → REFLECTION → MIND UPDATE
```

**Много целей одновременно** (треб. #21): Primary (Грильяж) / Secondary (voice
service) / Background (Telegram skill validation) / Low (self-optimization) —
attention решает, что получает активную когницию; задачи крутятся concurrently
без LLM-размышлений о каждой.

**NO fake continuous life** (треб. #22 — КРИТИЧНО): НЕ LLM непрерывно.
Continuous life = persistent state + persistent goals + background workers +
event processing + task supervision + memory + environment awareness +
autonomous scheduling. Большую часть времени система дешёвая; дорогая
когниция только когда полезна.

## 8. Фазы + зависимости + PRE/POST-Genesis

```text
CA-0 LongRunningTask (lifecycle расширение + Scope + Ownership)   [независимо]
CA-1 TaskAuthorizationLease (duration/environments/forbidden)     [CA-0,
     существующий lease store]
CA-2 TaskSupervisor + heartbeat/liveness + stall detection        [CA-0, CA-1;
     watchdog расширение]
CA-3 Checkpoints + resume-after-restart + TaskSteps persistence   [CA-0;
     E2 план]
CA-4 Task journal + real progress tracking                        [CA-0]
CA-5 Absence/return semantics + UserDisconnected ≠ TaskCancelled  [CA-4;
     RealtimeEventHub расширение]
CA-6 WAITING/RECOVERING состояния + parallel + bounded recovery    [CA-2]
CA-7 Continuous autonomy engine (Mind + TaskSupervisor,           [Mind MI-3+,
     много целей, attention)                                       CA-2/CA-6]
```

**PRE-Genesis допустимо**: CA-0 (lifecycle/scope — deterministic), CA-1
(lease — существующий store), CA-3/CA-4-минимум (checkpoints/journal —
persist-механика), CA-6 (WAITING). **ПОСЛЕ Genesis**: CA-5 (презентация
возвращения), CA-7 (полный continuous engine — Mind autonomy enforcement
E2/F + genesis upbringing).

## 9. Acceptance (треб. #62)

«Толик, доведи Грильяж до конца» → «Принял» → Ivan уходит → задача живёт
(журнал/progress/recovery) → «Дошёл до конца. Сначала починил X, потом упёрся
в Y, два варианта откатил, третий заработал. Тесты прошли. Оставил тебе список
изменений.» / «Не закончил, но не стоял без дела: 82%, последние 18% — X, вот
что попробовал» — из РЕАЛЬНОГО progress/journal (§50 no fake), через
personality. Целевой опыт — persistent digital being, который продолжает
работать в отсутствие владельца, помнит, объясняет, учится из опыта.