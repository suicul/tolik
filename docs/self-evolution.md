# TOLIK Self-Evolution — Self-Development, Self-Optimization, Experiment Lab (план, 09.10.2026)

PRE-GENESIS. Не имплементируется этим запросом (ARCHITECTURAL PLANNING).
Интегрируется в существующие системы — никаких параллельных архитектур.
Компаньоны: docs/computer-use.md (skills/games), docs/governance.md (capability
registry/autonomy/broker), docs/voice-identity.md.

## 0. Аудит: что УЖЕ есть (не дублировать)

| Механизм | Где | Отношение к self-evolution |
|---|---|---|
| Метрики | M7 VoiceTurnTelemetry (стадии turn'а, structured logs) | ПЕРВАЯ метрическая система — фундамент §21 |
| Benchmark-скрипты | scripts/benchmark-tts.py, scripts/calibrate-speaker-thresholds.ps1 | ПАТТЕРН benchmark-инфраструктуры (воспроизводимый, measured-only) |
| Learned rules | LearnedRuleStore (Postgres): provenance/confidence/bump+0.1 cap 0.95 | ПАТТЕРН накопления опыта с анти-drift — эксперименты (§32) |
| Task lifecycle | TaskLifecycle + pause/resume API (E-min) + watchdog + ProactiveStore | Фундамент long-running экспериментов (§14/§28) |
| Development agents | run_codex/run_opencode (agent) + CodexRunner/OpenCodeRunner wrappers | **Development Agent relationship УЖЕ ЕСТЬ** (§36) |
| Destructive screen | ContainsDestructiveDirective (agent) | rm -rf/format/shutdown → deny — техническая защита |
| Protected files | ToolEscalation: security-critical source → Critical+confirm; policy.json/CODEOWNERS → Destructive | **Protected Self-Modification УЖЕ ИМЕЕТ ТЕХНИЧЕСКУЮ основу** (§27) |
| Tests | 410/410 (Foundation.Tests) | Regression-защита (§31) |
| Deployment scripts | snapshot-tolik.ps1, rollback-postgres.ps1 (Docker-verified) | ПАТТЕРН canary/rollback (§12/§31) |
| Game detection | M8 ApplyGamePresence (heartbeat foreground) | Фундамент optimization budget (§30: игра → suspend) |
| Voice runtime modes | Night/Gaming/Silent | Бюджетные окна (ночь/игра → suspend) |
| Offline mode | TOLIK_MODEL_OFFLINE caps LowRisk | Resource/cost budget модель |

## 1. Три раздельные подсистемы (треб. §26 — КРИТИЧНО, не смешивать)

```text
SELF-MAINTENANCE   (здоровое обнаружение и ремонт)
   Health Monitor → Fault Detection → Diagnosis → Known Recovery
   → Restart/Repair → Verify                    [governance §4, already planned]

SELF-OPTIMIZATION  (улучшение ЗДОРОВОЙ подсистемы)
   Healthy → Measure → Optimize → Benchmark → Verify   [ЭТОТ план §2]

SELF-DEVELOPMENT   (новая capability)
   Missing Capability → Design → Implement → Test → Deploy  [ЭТОТ план §3]
```

Раздельные state-машины, раздельные хранилища, раздельные политики риска.
Оптимизация НЕ работает на сломанной подсистеме (сначала maintenance).

## 2. Self-Optimization (треб. §20-§26, §29-§33)

Каждая оптимизация = ЭКСПЕРИМЕНТ (никогда субъективная интуиция):

```text
Measure → Find Bottleneck → Profile → Generate Hypothesis
→ Create Experimental Branch → Implement → Benchmark
→ Regression Tests → Security Tests → Compare Against Baseline
→ Accept ONLY if objectively better → Canary → Health Check → Deploy
```

Отказ: latency лучше, но accuracy ниже → REJECTED (треб. §22: «быстрее, но
менее надёжно — НЕ оптимизация»).

### 2.1 Метрики (треб. §21) — SE-0

| Подсистема | Метрики | База |
|---|---|---|
| Voice | STT latency, speaker verify latency, intent latency, TTS TTFA, total | M7 telemetry ✓ (расширение стадиями) |
| Computer Use | screenshot, perception, OCR, target detection, planning, action, verification | CU-GA (после Genesis) |
| Core | API latency, DB query, serialization, IPC, allocations, startup | новые (dotnet counters/EventCounters) |
| Model usage | tokens, calls, context size, cache hit, reasoning cycles, expensive utilization | M7 + ModelRouter расширение |
| System | CPU/RAM/GPU/VRAM/network/disk | dotnet counters + agent heartbeat расширение |

### 2.2 Baselines (треб. §31) — SE-1

Baseline store (Postgres, паттерн LearnedRuleStore):

```text
Baseline: version, benchmark id, hardware env, software env, metrics, test results
Comparison: new version vs baseline по каждой метрике
Reject: unacceptable regressions в latency/reliability/accuracy/memory/CPU/
        GPU/security/compatibility
```

Benchmark-инфраструктура: воспроизводимые скрипты (существующий паттерн
benchmark-tts.py) + CI/тестовая интеграция: benchmark → сравнение → gate.

### 2.3 Experiment Lab (треб. §25) — SE-2

```text
Production / Development / Sandbox / Experiment
```

Experiment runner: N candidate-реализаций → benchmark все → выбрать лучшего
validated → отбросить неуспешные. Пример директивы: «Old STT / New A / B / C».
Sandbox = отдельная ветка/окружение (git worktree — уже используется как
безопасные лейны; docker-профили для изоляции).

### 2.4 Experiment memory (треб. §32) — SE-2

Store (Postgres): hypothesis/implementation/benchmark/result/reason
accepted|rejected/environment/regression info. Пример: «Оптимизация X
отклонена — снижала точность распознавания спикера» → не повторяется.

### 2.5 Optimization budget (треб. §30) — SE-3

CPU/GPU/RAM/VRAM/network/disk/time budgets (конфигурируемые,
TOLIK_OPT_* — паттерн TOLIK_VAD_*/TOLIK_GAME_PROCESSES). Предпочтение idle:

```text
User actively gaming → suspend (M8 game detection ✓ уже есть)
User working         → minimal background
Machine idle         → optimization allowed
Night / maintenance  → deeper experiments
```

Никогда не деградировать нормальную нагрузку без authorization.

### 2.6 Continuous loop (треб. §33) — SE-6

```text
Observe System → Collect Metrics → Detect Bottleneck/Repetition/Missing
→ Classify: Maintenance | Optimization | New Capability
→ Proposal → Prioritize → Experiment → Validate → Deploy
→ Measure Again → Learn From Result → Repeat
```

Обратная связь непрерывна; результаты в experiment memory (§2.4).

## 3. Self-Development (треб. §19, §36) — SE-5

Новая capability: «Мне не хватает возможности X» → сама добавить. Через
СУЩЕСТВУЮЩИЕ механизмы (никаких новых):

```text
TOLIK (decision/planning/personality)
   ↓ Self-Improvement Planner (существующий TaskLifecycle + confirmations)
   ↓ Development Agent (СУЩЕСТВУЮЩИЙ: run_codex/run_opencode — §36)
   ↓ Repository (isolated branch/worktree)
   ↓ Sandbox (Experiment Lab §2.3)
   ↓ Tests / Benchmarks / Security Validation
   ↓ Deployment Gateway (snapshot/rollback паттерн + canary)
   ↓ Health Check → Verify → Accept / Rollback
```

Полный pipeline (треб. §19): Need Detection → Capability Proposal →
Architecture Analysis → Implementation Plan → Development Sandbox → Code
Generation/Modification → Tests → Build → Integration Tests → Security
Validation → Canary → Health Check → Deploy → Verify → Accept/Rollback.

Ограничения: unrestricted self-modification НИКОГДА; каждая ступень — через
существующие authorization/audit; риск-классификация §4 ниже.

## 4. Protected Self-Modification (треб. §27) — SE-4

**Техническое принуждение** (не только LLM-инструкция «не модифицируй security»):

- СУЩЕСТВУЮЩЕЕ: ToolEscalation → security-critical source files
  (AuthorizationPolicy.cs, TolikRoles.cs, AuditWriter.cs, ProtocolContracts.cs...)
  → Critical+confirm; policy.json/CODEOWNERS → Destructive.
- **Расширение**: protected-path rules для НОВЫХ security-компонентов
  (AutonomyPolicy.cs, CapabilityRegistry.cs, VoiceAdaptationService.cs,
  GenesisVoiceEnrollment.cs, TrustedPersonEnrollment.cs, SignedTransport.cs,
  GenesisService.cs, VoiceIdentityDiagnostics.cs) — тот же механизм
  (escalation rules — единый реестр).
- Protected areas: Identity/Owner/Roles/Ceilings/Authorization/Broker/Secrets/
  Audit/Emergency Stop/Genesis/Trust model/signed transport/core authorization.
- Self-development МОЖЕТ инспектировать (read-only); ИЗМЕНЕНИЯ — только через
  явный owner-controlled release path: proposal → owner approval (HIGH) →
  backup → canary → health → production.
- Репозиторий/build/deployment границы: protected-файлы не проходят
  автоматический deploy; canary-gate требует owner-подписи для protected.

## 5. Risk-based autonomy для улучшений (треб. §29)

Интеграция с существующей AutonomyPolicy (governance §2) — улучшения
классифицируются той же машиной:

| Категория | Примеры | Автономия |
|---|---|---|
| LOW | internal optimization, caching, refactoring, harmless perf | валидация → auto-deploy (после достаточной истории успехов) |
| MEDIUM | core workflows, model routing, внешние интеграции | stronger validation + owner approval |
| HIGH | security/privilege/identity/secrets/authorization | explicit owner-controlled approval |
| CRITICAL | ослабление контроля владельца / unrestricted access | NEVER auto-deploy |

Risk приписывается PROPOSAL'у (детерминированно: затронутые protected-файлы →
HIGH; только перф/кэш → LOW; иначе MEDIUM) — не LLM'ом.

## 6. Personality / Cephalon (треб. §34, §35)

- Personality описывает улучшения («нашёл мерзкий memory leak»), шутит над
  проваленными экспериментами — НО НИКОГДА не влияет на authorization/security/
  deployment safety/risk classification (разделение — как governance §6).
- Cephalon состояния: Learning/Thinking/Profiling/Experimenting/Building/
  Testing/Deploying/Waiting/Success/Regression/Error/Rollback →
  RealtimeEventHub; визуал не влияет на authorization.

## 7. Фазы, зависимости

```text
SE-0 Метрики (voice ✓ база + ComputerUse/core/model/system)   [независимо]
     — DONE 10.10.2026 (commit ccfd2b0: MetricCatalog — фиксированные
     определения 20 метрик по подсистемам (voice M7-расширение/core/model/
     system), неизвестные fail closed; MetricRecorder — bounded rings 256,
     MetricSummary percentiles min/p50/p95/max; Computer Use — CU-GA
     post-Genesis)
SE-1 Baseline store + benchmark infra + CI gates              [SE-0]
     — store/comparison/gate DONE 10.10.2026 (commit ccfd2b0: BaselineRecord
     (version/benchmark/env/metrics/test results); BaselineComparer —
     per-metric сравнение, lower-is-better vs higher-is-better, tolerance
     ±10% + MetricThreshold, missing metric FAIL CLOSED; BaselineStore
     (Postgres, LearnedRuleStore паттерн) + EF-миграция AddBaselines
     (аддитивно, применится на следующем рестарте core); benchmark
     infra + CI gates — с SE-2)
SE-2 Experiment Lab (sandbox + runner + experiment memory)    [SE-1]
SE-3 Optimization budget (TOLIK_OPT_* + game/idle окна)       [SE-0]
SE-4 Protected self-modification (escalation extension +
     owner release path)                                      [независимо]
     — escalation extension DONE 10.10.2026 (commit ccfd2b0: security-
     critical-source правило расширено 11 новыми security-компонентами —
     CapabilityRegistry/AutonomyPolicy/AutonomyLevelPolicy/ConsentModel/
     CoreLease/SignedTransport/GenesisService/GenesisVoiceEnrollment/
     TrustedPersonEnrollment/VoiceAdaptationService/VoiceIdentityDiagnostics
     — единый реестр, Critical+confirm, T13; owner release path — с SE-5)
SE-5 Self-Development pipeline (через существующие dev agents) [SE-2, SE-4]
SE-6 Continuous loop (engine: classify/prioritize/experiment)  [SE-2, SE-5]
SE-7 Cephalon + personality integration                       [SE-6]
```

Решения ДО Genesis: SE-0 (метрики — voice база уже есть), SE-1 (baseline
store), SE-4 (protected расширение — техническая защита). ПОСЛЕ Genesis:
SE-2/SE-3 (лаборатория/бюджеты), SE-5/SE-6 (само-развитие/цикл), SE-7.

## 8. Риски и митигации

| Риск | Митигация |
|---|---|
| Само-модификация ослабляет security | Protected escalation (§4) + owner release path + canary |
| Плохая оптимизация в production | Эксперимент-цикл: baseline + regression + reject (§2) |
| Повтор проваленных экспериментов | Experiment memory (§2.4) |
| Бюджеты съедают нагрузку владельца | §2.5: game/idle окна (M8 detection) + authorization |
| Personality обходит security | Разделение (§6) — тестами |
| Рекурсивное само-улучшение | Bounded: одна эксперимент-ветка за раз, MaxAttempts, STOP+ask |

## 9. Acceptance UX (треб. §37)

«Толик, изучи эту программу» → reuse → demonstration → game assistance →
new capability («пока нет, но могу добавить») → self-optimization («чем
занимался ночью?» → «сократил задержку на 18%, прогнал регрессионные тесты;
одну оптимизацию откатил — была быстрее, но ломала точность»). Личность
иллюстративна; архитектура — под ней.
