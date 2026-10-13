# TOLIK Master Plan (30.09.2026)

Living plan: status + next actions per track. VPS migration is distant-future
by owner decision — not on the active list.

## ⚡ PLAN REFORM (директива владельца, 12.10.2026): PRE-GENESIS MAXIMIZATION

> «Большинство функционала кроме VR надо сделать НЕ post-Genesis, а pre-Genesis.
> До проведения церемонии должно быть как можно больше функционала и меньше ошибок.»

**Принцип переклассификации: «механика до церемонии, рождение — на церемонии».**
Всё детерминированное/тестируемое/интегрируемое ПЕРЕЕЗЖАЕТ в pre-Genesis.
Авторизация/приватность НЕ ослабляются (AGENTS SECURITY-модель без изменений).
**Эта секция SUPERSEDES post-Genesis пометки в треках ниже.**

**ПЕРЕЕЗЖАЕТ в pre-Genesis (бывшие post-Genesis):**
```text
Track 3:  CE-3 (реальный TTS-спектр → FFT → bands), CE-4 (desktop awareness),
          CE-5 (Expression DSL — bounded/validated/sandboxed)
Track 9:  C (Windows Privilege Broker), D2, E2, F (Emergency Stop), G, H2;
          CU-3..CU-10 (closed-loop, browser, skills, exploration, demo-learning,
          long-running, cephalon) + CU-GA механика (goal runner, GAME_LEARNING_MODE,
          observation hierarchy) — действия всегда через существующие authorization gates
Track 10: SE-2 (Experiment Lab: sandbox/runner/experiment memory), SE-3 (бюджеты
          TOLIK_OPT_*), SE-5/SE-6 (self-development pipeline + continuous loop),
          SE-7 (Cephalon personality integration)
Track 11: MI-3..MI-8 (DriveSystem→Motivation→Activity; Goals/Attention/Uncertainty/
          Reflection; Emotion из переходов + Mood + VoiceEmotion; Journal; Proactive
          policy; Self-model + Mind Inspector) + MI-9 seed-МЕХАНИЗМ (активация — на
          церемонии) + MindState ЭКСПОРТ В HOST (цефалон выражает реальное состояние
          ума ДО церемонии — теперь явная цель pre-Genesis)
Track 12: CA-2 (TaskSupervisor/heartbeat/stall), CA-5 (absence/return), CA-7
          (continuous engine)
Track 13: MM-4..MM-6 (мобильное зрение/камера — permission+индикатор §50 всегда)
Track 14: RA-3 (Reward system: типы/memory/anti-reward-hacking/Effort-vs-Result —
          machinery; реальные reward-события живут с реальным использованием)
Track 15: IP-3 (Identity Cryptography), IP-5/7/8 (presence transitions, миграционные
          части)
Track 16: AN-P5 (камера в Android), AN-P7 (Presence в Android)
```

**ОСТАЁТСЯ post-Genesis (ceremony-constitutive / owner-bound / VR):**
```text
- СА ЦЕРЕМОНИЯ Genesis (MFA, создание Identity, TOLIK_DEV_AUTO_TOKEN off,
  voice phase, живой enrollment владельца/Maria — выходные)
- RA-4 (Genesis FIRSTS — первый успех/провал/self-discovery рождённой сущности:
  по определению существуют только после рождения)
- GU-0..GU-7 (Genesis Upbringing — фазы воспитания рождённой сущности)
- VR (Meta Quest 3S — явно исключено владельцем из pre-Genesis)
- Живая голосовая верификация (требует владельца с микрофоном)
```

**Порядок реализации (по ценности/зависимостям; каждая фаза — build+tests+audit,
меньше ошибок = дисциплина качества не ослабляется):**
```text
PRG-1  Mind Activation: MI-3..MI-6 + MindState экспорт в host → цефалон выражает
       реальный ум; наибольшая видимая ценность, строится на MI-0..2 + expression pipeline
       — ✅ **COMPLETE 12.10.2026 (746/746; реализовано оркестратором после stall-кластера
       fixer-лейнов)**: **1a** (65e37dd): MindRuntime (MI-0 state + MI-2 drives на
       РЕАЛЬНЫХ входах; 1 Hz host-сервис — ум живёт независимо от подключённых;
       live-verified end-to-end: /chat → attention 0.655 в выражении) + Cephalon
       wiring; **1b** (4c21717): MI-5 EmotionEngine (импульсы из переходов; Mood EWMA
       ~30мин Valence/Arousal/Momentum) + MI-6 MindJournal (ring 512; фиксированные
       kinds — неизвестные ОТВЕРГАЮТСЯ, приватность by-construction; русские
       плюрализмы; GET /mind/journal); **1c** (90ec890): MI-3 DriveSystem (15 драйвов;
       boredom-производные TOLIK_DRIVE_*; LOCKED → Safety spike) + ActivitySelector
       (фиксированный каталог; utility + Idle-baseline «idle — валидный результат»;
       cooldown анти-луп; LOCKED ограничивает; journal только переходы); **1d**
       (98579b4): MI-4 MindGoals (занятия → цели: dedup per type; satisfaction из
       РЕАЛЬНЫХ правил — work drains/curiosity decays/timeout; max 8) + Attention
       арбитраж (user > task > occupation > idle — routing only) + Uncertainty
       (провал +0.20, decay; WIRED в ExpressionState — цефалон показывает реальную
       неуверенность) + Reflection (детерминированная статистика исходов, никогда
       не выдуманные уроки; RecordGoalOutcome валидирует каталог+литералы).
       Исполнение не-idle кандидатов (selection готова) — с SE-5
PRG-2  CE-3/CE-4/CE-5: голос-реактивный цефалон (FFT-спектр) + desktop awareness + DSL
       — ЧАСТИЧНО DONE 12.10.2026 (754/754): **CE-3** (abc9277): VoiceSpectrumState —
       реальный исходящий TTS-поток → 512-sample ring + hand-rolled radix-2 FFT
       (ноль зависимостей) → амплитуда + полосы (low<500→оболочка, 500-2500→глаз,
       2500-8000→частицы) → ExpressionState (были честные нули с CE-2); WAV-парсер
       (RIFF/16-bit/mixdown stereo; MP3-fallback — честный skip, не мусор); EWMA-
       сглаживание; честная тишина (~300ms → нули — паузы читаются как паузы);
       5 тапов на всех исходящих голосовых путях (/voice/process, /voice/turn,
       /voice/turn-stream ×2, simple-intents helper). Тело цефалона теперь движется
        голосом TOLIK. **CE-4 DONE (084457e, 789/789, оркестратор):** Desktop
        Awareness — авто-режимы присутствия: ModeArbitration (ЧИСТЫЙ; ручной выбор
        владельца в трее НЕПРИКОСНОВЕНЕН; Emergency/Hidden/Focus — owner-only,
        никогда авто) + DesktopWatcher (ТОЛЬКО безопасные факты: имя процесса
        переднего плана + fullscreen-прокси; НИКОГДА контент окон — приватность
        by-construction; TOLIK_GAME_PROCESSES паттерн M8) + App wiring (2s poll →
        арбитраж → SetMode(auto:true); ручной клик пинит). Fullscreen игра → Gaming;
        другой fullscreen → Presentation; конец → Normal. **CE-5 DONE (4750df9,
        801/801, оркестратор): Expression DSL** — декларативные рецепты выражений:
        «сухая насмешка» = bounded parameter DELTAS (фиксированный whitelist полей
        ExpressionState, |delta| ≤ 0.5, provenance обязательна, версии строго
        монотонные, capacity 128 — full REJECTS); LLM может ПРЕДЛАГАТЬ (данные) —
        детерминированная валидация решает; НИЧЕГО не исполняется нигде (bounded/
        validated/safe, §14); применение = ADDITIVE blend пре-композер (принцип
        одного смешанного выражения; defense-in-depth в применении); store с
        usage/feedback статистикой (сигнал для CE-6 habit-formation, никогда
        permission); 4 auth-эндпоинта (store/activate/deactivate/feedback).
        **PRG-2 COMPLETE (CE-3+CE-4+CE-5).** Реакция на курсор/уведомления —
        расширенная awareness позже
PRG-3  CA-2/CA-5/CA-7: TaskSupervisor + continuous engine (механика непрерывности)
       — ✅ **COMPLETE 12-13.10.2026 (804/804; оркестратор)**: **CA-2** (92f4e5c):
       TaskSupervisorService — RUNNING-таск watchdog (живость = lifecycle-события +
       CreatedAt честный пол; bounded-лестница L1 mind/L2 notify/L3 PAUSE — никогда
       auto-cancel); **CA-5** (3dfcb2c): return-семантика (UserDisconnected ≠
       TaskCancelled архитектурно; presence_return сводка при reconnect; социальный
       бамп); **CA-7** (cb0b7d6): continuous engine v1 — СОБРАННЫЙ loop (ум 1 Hz →
       занятия → цели → attention-арбитраж → supervisor) + **ИСПОЛНЯЮЩИЙСЯ
       SystemReview** (оккупация производит детерминированный отчёт → ReviewResult;
       no-fake инвариант: review существует только при реальном SystemReview);
       honest boundary: task-creating loop (mind→planner→authorization) ждёт
       governance E2/F — зависимость самого плана
PRG-4  Governance C/D2/E2/F/G/H2: broker + emergency stop + self-healing (safety-механика)
       — ЧАСТИЧНО DONE 13.10.2026 (811/811; оркестратор): **Phase G Emergency Stop**
       (2c9a2f0): EmergencyStopState (владельческий soft kill-switch ≠ LOCKED —
       управляемая пауза автономии; in-memory by design: рестарт = естественный
       сброс); AutonomyPolicy: EmergencyStopped → НОВЫЕ privileged ops DENY
       (Modify/Destructive/Critical, ВКЛЮЧАЯ pre-authorized rules — пауза
       аутранки пре-авторизацию; только ограничение); READS ЖИВУТ (владелец
       сохраняет канал chat/ask/resume); ум → Idle-only оккупации (остановленный
       ум отдыхает — никогда не позирует); /emergency/stop + /emergency/resume
       (owner-only, audited; stop паузит ВСЕ running-таски через существующий
       lifecycle); /health + emergencyStopped. Остаток: **C** (Privilege Broker —
       admin-token service + C0 IPC spike), **D2** (Repair Mode capabilities),
        policy: whitelist + priorities; config-change NEVER; rate-limit. **E2 DONE
        (c0b7f47, 818/818, orchestrator)**: NotificationPolicy + Gate — proactive
        notifications are policed: the fixed whitelist of informative kinds with
        priorities; «я решил изменить конфигурацию» — НИКОГДА a notification (a
        permission request routes to the confirmation flow); unknown fails closed;
        rate-limit per-category + global (TOLIK_NOTIFY_*, honest drops, rejects
        never burn the budget); the supervisor stalls + the reminders wired
        through the gate (the repeat spam capped). Notifications INFORM — never
        authorize. **H DONE (7085547, 830/830, orchestrator)**: the per-op file
        tiers (посмотреть ≠ удалить: Read/Write = the roots; Move/Delete — the
        protected .tolik/.env/.git NEVER; Execute = the executable extensions
        only); the network allowlist (empty = localhost-only fail-closed; the
        wildcard subdomains; the rate gate per-host + global); the SECRET OPAQUE
        HANDLES (the string form is ALWAYS [secret:name] — structurally never the
        value; UnwrapValue = the executor explicit edge call; the declared slots
        whitelist). **D2 DONE (b2e95f3, 838/838, orchestrator)**: RecoveryRegistry
        — the TYPED fixed five (voice-restart/tts-fallback/component-restart/db-
        restore-SINGLE-SHOT/task-stuck→CA-2), each routing an EXISTING capability
        (the authorization pipeline mandatory for every recovery action; extension
        = a code change, never a runtime registration); RecoveryAttemptGate —
        bounded attempts (2 → STOP → explain → ask; the verification resets; the
        owner re-arms; the budgets never shared); InstallTolikUpdate — CRITICAL +
        MFA + confirm, never background, NEVER self-invoked; UpdateIntegrityPolicy
        — the SIGNED manifest mandatory (an unsigned blob NEVER installs, T13).
        **F DONE (50dbc34, 844/844, orchestrator)**: PersonalityPhrasing — the
        HUMAN phrasings over the ALREADY-MADE verdicts (Personality ≠
        Authorization, треб. #16/#17): Deny «за пределами полномочий, не
        обсуждается» (never softened); ConfirmationRequest «Ща, надо подтверждение:
        действие — цель, риск, последствие» (треб. #19: never «CONFIRM ACTION ID
        84729»; the bound-confirmation SHA-256 underneath unchanged); TaskResult
        «Готово:/Есть проблема: … Разберусь»; TaskPausedForStall «всё сохранено»;
        the FIXED templates = the human tone even offline (the LLM may phrase
        richer later, always over the same verdicts); Emotion ≠ permissions
        STRUCTURALLY TESTED (the AutonomyContext has NO emotional fields; the
        phrasing API returns strings — no overload accepts a principal/decision:
        «the personality deciding» is unrepresentable). **C0 SPIKE DONE (bd75453,
        850/850, orchestrator) — PRG-4 COMPLETE**: BrokerProtocol — TYPED ops
        ONLY (structurally NO string-command field — reflection-tested); the
        FIXED restart allowlist (the RecoveryRegistry names); ElevateCommand —
        DENY BY DEFAULT (the explicit enable + CRITICAL/MFA/Owner = the double
        gate; the spike NEVER executes); BrokerServer — NamedPipeServerStreamAcl
        with the CURRENT-USER-ONLY descriptor (the OS refuses other users before
        the protocol starts — the ACL IS the identity gate), ONE op at a time;
        BrokerClient — the typed round-trip. The REAL NAMED-PIPE round-trips
        tested live (the correlation + the allowlist verdicts through the pipe).
        The spike lessons: the per-test unique pipe names (the orphaned-server
        interception!), the client hard timeouts, the stuck MSBuild nodes after
        the timed-out runs. The FULL Phase C (the actual privileged SCM calls,
        the --broker service launch, the Core→Agent→Broker chain) — the follow-up
        per the spike choice: NAMED PIPES confirmed
PRG-5  MI-7/MI-8/MI-9-seed + RA-3: proactive policy + self-model + seed + rewards
       — ЧАСТИЧНО DONE 13.10.2026 (Foundation green; оркестратор): **RA-3**
       (274a48b): RewardStore — bounded ring 256 RewardEvent(At/Type/Summary/
       Source); **ANTI-REWARD-HACKING СТРУКТУРНО**: Source-provenance обязательна
       (ArgumentException, не фильтр — TOLIK никогда не награждает себя без
       реального источника), store без authority (рефлексия: ни один метод не
       принимает/возвращает AutonomyDecision/SessionPrincipal — награды
       ИНФОРМИРУЮТ, никогда не грантуют); Effort-vs-Result честно раздельно
       (Achievement vs EffortAcknowledgement — проваленная работа получает усилие,
       НИКОГДА фейковое достижение); **MI-7** (274a48b): ProactivePolicy — TOLIK
       НИКОГДА не прерывает пользователя (5-мин тихое окно), дневной бюджет
       жёсткий (default 6), приоритеты (SystemSignal достижимы, CuriosityShare
       только при boredom>=0.5 и бюджете 2/3, всё прочее DEFAULT DENY — idle
       валиден), locked глушит полностью; E2 gate — финальный, оба должны
       разрешить. Остаток: **MI-8** (Self-model + Mind Inspector), **MI-9-seed**
       (Genesis seed МЕХАНИЗМ — активация на церемонии), **RA-3 остальное**
       (Cosmetic/Creative/SharedExperience типы — additive; Cephalon-интеграция)
PRG-6  CU-3..CU-10 + CU-GA: компьютерное использование (действия через authorization)
PRG-7  SE-2/SE-3/SE-5/SE-6/SE-7: экспериментальная лаборатория + само-развитие
PRG-8  IP-3/5/7/8: identity-криптография + presence-переходы (миграционная готовность)
PRG-9  MM-4..6 + AN-P5/AN-P7: мультимодальность + мобильные органы
```
Статус PRG-фаз отмечается в треках ниже по мере реализации.

## Model policy (owner-approved 30.09.2026, cloud keys live since)

- Chatter/voice (Simple): **qwen3:4b local**, `think:false`, `num_predict:250`, `num_ctx:8192`.
- Reasoning/Coding: **cloud gpt-6.1-sol** (tooken.club, key in `.env`), offline fallback.
- Measured: qwen3:4b @100% GPU = ~100 tok/s; default 256k ctx drops it to 77% CPU / ~18 tok/s.
  `OLLAMA_NUM_CTX=8192` is set in user env AND forced per-request by the router.
- Ask-policy for coding (cloud vs local + remembered preference): DONE 02.10.2026 —
  `ModelPreferenceStore` (in-memory per-user; restart clears, ask re-presented) +
  `ResolveWithFallback(kind, preference)` override (Local → offline-first coding,
  Cloud → cloud-first default, Simple unaffected) + `POST/GET/DELETE /models/preference`
  (authenticated, per user, invalid mode → 400); wired into /chat + /voice/turn.

## Track 1 — Assistant core ✅ mostly done
Done: API/auth/memory/tasks/planner/AutoPlan/partial-failures/audit+read/
agent transport/Windows agent/CLI/Linux-shim/proactive (reminders+watchdog)/
MFA/speaker enroll+verify+guest mode/persona/242 tests/green (146 on 30.09 → 242 now, +4 MemoryOrigin +2 forbidden-surface +2 strict-envelope +18 roles/ceilings +9 lease +10 locked-state +8 model-preference +10 transport-frame +15 family/enrollment +8 sanitizer +4 key-exchange +6 genesis-ceremony tests, verified green).
Genesis ceremony subsystem DONE 03.10.2026 (Phase 6): preflight + instance identity
table + owner binding + atomicity + rerun impossibility.
PRE-GENESIS hardening pass DONE 03.10.2026 (live): core aligned + миграция
20261002174934 применена (tolik_genesis_identity пуста, ceremony не запускалась),
memory separation gate: 0 development-строк на live, smoke PASS (origin round-trip,
genesis gates fail-closed: 400 preflight_failed без MFA/backup), pre-genesis backup
validated (tolik-backup-pre-genesis.dump, ~2.05 MB, SHA256 sidecar; файл во временной
папке — переместить в постоянное хранилище перед церемонией).
Осталось перед READY_FOR_GENESIS (внешние действия владельца на момент церемонии):
TOLIK_DEV_AUTO_TOKEN off, backup/restore attestations, MFA код, запуск ceremony.
Next: enroll wife's voice + family role scoping (§18, wife = scoped scenarios).
Family scoping DONE 02.10.2026: roles TrustedGuest/Family + ceilings + minimum-role
matrix (Codex/OpenCode/RunProcess→Owner) + owner-issued enrollment tokens
(POST /auth/enrollment-token → POST /auth/enroll, single-use, TTL 10 min, audited).
Next: wife's actual enroll (external action — mint a token, run enroll on her device).

## Track 2 — Voice ✅ working, quality loop open
Done: STT (faster-whisper, `language` param), XTTS-v2 GPU sidecar approved by ear
and enabled by default, DSP chain (off), whisper dataset pipeline (985 clips,
medium/en transcripts, review pending).
Next: review.tsv curation → owner's CosyVoice fine-tune (owner's track);
normalization dictionary (Цетус/Гринир seed list ready); game-mode mute.

## Track 3 — Native Windows UI (WPF, approved) 🔨 IN PROGRESS — Cephalon Desktop Embodiment (master directive 10.10.2026)
Cephalon overlay: transparent always-on-top, idle/listening/thinking/speaking/
working/denied states, direct mic (no browser), mic-always-on WITH indicator
(approved), game mode silent. Web UI stays as fallback. Voice-ID enroll wizard exists in Web UI (3 phrases +
verify); port it. Needs: wake-word (openWakeWord) integration,
challenge-response for sensitive voice actions.
**UNBLOCKED (10.10.2026):** новый master directive владельца — Cephalon Desktop
Embodiment & Emergent Expression System. Мастер-план: **docs/cephalon-desktop.md**
(границы подсистем; tech choice: WPF + CPU процедурный 2D — RTX 3060 остаётся
свободной для игр, без новых NuGet-зависимостей; геометрия параметрическая →
перенос в VR/3D). Фазы CE-1..7 (presence prototype → state-driven → voice-
reactive → desktop awareness → DSL → learned → multi-device).
**Реализовано (pre-Genesis):** **CE-1 DONE (commit 854a8d8, designer-лейн):**
Desktop Presence Prototype — WPF оверлей (transparent/borderless/topmost,
PerMonitorV2 DPI, click-through passive ↔ interactive, tray + Ctrl+Alt+T,
multi-monitor), оригинальная процедурная композиция (слоистая оболочка: 6 arc
сегментов + 3 деформируемых arc + corner brackets + energy spokes; глаз: facet
iris — угловая сигнатура TOLIK, pupil с rim/glint, механические shutter веки;
energy motes), **процедурный idle с ограниченной стохастичностью** (saccades
2-6.5s, blink 3.5-9s, pulses, drift, breathing — seeded, bounded, без повторного
цикла), 7 режимов присутствия (typed policy table; Hidden=5fps frozen; Gaming/
Presentation force click-through), FrameGovernor (FPS cap + EWMA adaptive
quality, degrade fast/recover slow), honest debug («core: local, no Core
connection» — НИКОГДА фейковое состояние). Инварианты: renderer чист (нет
identity/permissions/network — проверено); красный ТОЛЬКО из Error/Locked (реальные события, Phase 2/3).
**CE-2 ПОЛНОСТЬЮ DONE (Core: commit 4def5df; Desktop client: commit eeb9592,
685/685 тестов, live-verified end-to-end):** Core — CephalonExpressionService
(5 Hz, per-user: реальные источники — задачи/locked/voice-in-flight/присутствие;
mind — post-Genesis честные нули; publish gating delta>0.01/mode/1s heartbeat)
+ РЕАЛЬНЫЙ БАГФИКС: Program.cs создавал второй RealtimeEventHub — DI-издатели
(ProactiveReminder) вещали в мёртвый hub; исправлено. **Wire-контракт live-
verified** (WS smoke test): {"kind":"cephalon_expression","expression":{16
camelCase полей + mode}}. Desktop — Bridge/ (WS-клиент: token через GET
/auth/local-token, in-memory, URL-encoded; reconnect capped 30s; **401-fresh =
terminal ноль ретраев**), ExpressionBridge (все 7 мод → states, каждое поле на
свою ось; asymmetry честно НЕ замаплена — нет оси, никогда не фейкается),
ExpressionLinkPresenter (honesty: no-core / connected-awaiting / AUTH REJECTED
отличимо / **stale >15s → local idle, никогда замершая поза**), animator
eased-targets (~5 Hz события = TARGETS, никогда snap) + SpectrumIntensity
(честный ноль до CE-3) + ambient wobble (композер делегирует стохастику
renderer'у — §7). +78 desktop тестов (wire/маппинг/reconnect/encoding/token-
redaction). Остаток: **CE-3** (реальный TTS спектр: amplitude/bands честно
нулевые пока voice-service не отдаст телеметрию), CE-4 (desktop awareness),
**Блокер снят:** прежний stall designer-линии (04.10, три подряд, ноль записей)
не повторился — свежая сессия работает.
**CE-1b: ЦЕФАЛОН В 3D (директива владельца 13.10.2026 «более объемным, попробовать
3д» + «скобы видны — убрать»; commit 891b0f7, 774/774):** софтверный 3D-рендерер
(НИКОГДА GPU — RTX 3060 свободна для игр; ноль новых зависимостей): V3/Mat4 +
процедурный параметрический меш (CephalonMeshBuilder — ЕДИНСТВЕННЫЙ дизайн-источник
для VR/Android 3D-портов, docs/VR.md) + Cephalon3DPipeline (анимация вершин →
перспектива → backface culling → key-light flat shading с эмиссией → depth-sliced
painter's algorithm; детерминированный, allocation-free hot path). Тело: кольца
arc-band на РЕАЛЬНЫХ z-глубинах с независимым вращением; глаз = сфера с гранёным
iris + офсетный зрачок + glint; ПОЛУСФЕРИЧЕСКИЕ шторки-веки (механический блинк =
настоящая 3D-окклюзия); 3D-орбиты энергии. **Скобы УБРАНЫ** → 2-3 дуговых сегмента
внешнего кольца (естественная часть тела). Composition: −496/+161 — 3D draw-list →
WPF кисти; LOD-лестница из FrameGovernor; все честные состояния/ум/спектр без
изменений контракта. des-7 умер mid-write (провайдер-таймаут) — оркестратор
добыл: 2 компиляционных фикса + InternalsVisibleTo + 8 тестов (детерминизм/
painter's ordering/finited bounds/fail-closed/LOD/motes).

## Track 4 — Memory++ (biggest quality lever after voice)
1. pgvector + embeddings (associative recall; Postgres ready).
2. Auto event extraction from conversations (promises/facts without «запомни»).
3. Priorities/decay/contradiction handling; expires_at schema gap.
4. Cross-user knowledge map.

## Track 5 — Autonomy (supervised self-coding)
Have: run_codex/run_opencode steps, build/test gates, confirmations.
Missing: replan-on-failure loop, own-diff review, rollback, sandbox scopes
(never self-edit authorization). Order: propose-diff → build+test → ask;
then autonomy inside allowlisted dirs.

## Track 6 — Perception & clients (later)
Vision/screen understanding (zero), Android client (zero, Kotlin),
desktop tray (part of Track 3), expires_at, owner notifications channel.

## Track 7 — VR client (Meta Quest 3S, approved 05.10.2026) 🔲 strategy done, implementation after Desktop Phase 1
Strategy: **docs/VR.md** (решения владельца, архитектура, фазы VR0-VR6). Thin-клиент:
passthrough + объёмный интерактивный цефалон (3D, продолжение дизайн-системы Desktop) +
виртуальный монитор (стрим окна OpenCode с ПК, цель <100ms LAN) + встроенная
проекционная клавиатура шлема. Паринг по QR разово: plugin-мост OpenCode → Core →
шлем (шлем подключается ТОЛЬКО к Core, QR выдаёт Core; авторизация/ревокация в Core;
device identity §10/§11). Стек: Unity + Meta XR SDK (C# — совпадает со стеком TOLIK).
Фазы VR0(recon)/VR1(паринг)/VR2(цефон 3D)/VR3(монитор)/VR4(ввод)/VR5(голос)/VR6(polish)
— после Desktop M9-M13. Смарт-очки (Ray-Ban Meta Display: Web Apps через QR +
Wearables Toolkit 1.0; Xiaomi — research): docs/VR.md §8, после VR-трека.

## Track 8 — Voice Identity: Genesis Enrollment + Adaptive Speaker Profile (approved 07.10.2026) ✅ implementation DONE 08.10.2026, live voice verification pending (owner's weekend)
Full plan: **docs/voice-identity.md**. VI-0..VI-7 все DONE: VI-0 калибровка
(329 клипов, measured), VI-1 VoiceProfile v2 (canonical anchors + bounded
adaptive ≤8/TTL 30d/Wc 0.7), VI-2 async адаптация (channel вне turn-пути,
update только существующих профилей), VI-3 Genesis voice phase (5 фонетических
фраз, quality/consistency, /genesis/voice/*), VI-4 household (EnrollTrustedPerson
интент, /voice/person/enroll Owner+High gate, role Family/TrustedGuest НИКОГДА
Owner, distinctness vs все профили), VI-5 recovery/diagnostics (toggle/reset/
delete/speakerTier+Reason), VI-6 emotion контракт (без фейка). Anti-contamination
тройная защита (tier 0.60 > accept 0.45 + bounded layer + distinctness). 388/388
green (+49 тестов к Track 8). Живые голосовые тесты (реальный enroll владельца,
Maria, ceremony) — выходные владельца.

## Track 9 — Jarvis Foundation: Governance + Computer Use (approved 08.10.2026) ✅ PRE-GENESIS scope DONE 09.10.2026, post-Genesis items queued
Two companion plans from owner directives, grounded in a full code audit (exp-9), plans approved by owner:
- **docs/governance.md**: Capability Registry (единый источник: descriptors + declared-risk + escalation merge; **GAP закрыт — GuardIntentCapability на всех интент-путях**), Autonomy Policy (AUTONOMOUS/NOTIFY/CONFIRM/MFA_CONFIRM/DENY поверх существующей policy, context-aware, pre-authorized rules), Windows Privilege Broker (план, после Genesis), Self-Healing/Repair (план), Background Tasks v2 (E-min DONE: pause/resume API + Paused→Running), Personality≠Authorization (план), Emergency Stop (план), File/Network/Secret boundaries.
- **docs/computer-use.md**: CU-1..CU-10 (после Genesis): Computer Use Foundation, UI Observation (a11y/OCR/vision), UI World Model, Closed-loop, Sensitive UI + prompt injection, Browser Automation, Skill Learning (semantic), Exploration Mode, Demonstration Learning, Long-running, Cephalon. **+ §15 Game Automation & Interactive Application Layer (CU-GA, директива 09.10.2026):** игры как специализация Computer Use (среда БЕЗ a11y-гарантий: DirectX/анимации/cooldowns), Game World Model (семантические таргеты первичны, координаты = execution detail), closed-loop с верификацией перехода состояния (клик никогда не считается успешным), GAME_LEARNING_MODE (семантические workflow, НЕ координаты; demonstration → валидация автономным воспроизведением → только verified → trusted), goal-oriented tasks («фарми до 100М» — цели, не макросы), WaitUntil-условия вместо sleep, dynamic UI adaptation (re-identify при апдейтах), safety НЕ ослабляется «потому что игра» (анти-cheat bypassing/DRM ЗАПРЕЩЕНЫ — AGENTS.md §25), иерархическое наблюдение (дорогая модель только при значимом изменении сцены), skill confidence states (LOW_CONFIDENCE → нет risky autonomous). ДО Genesis: контракты без фейка; ПОСЛЕ: провайдеры/GAME_LEARNING_MODE/goal runner/skills.
- **Реализовано (PRE-GENESIS scope, 410/410 green):** Phase A (CapabilityRegistry + gap-fix, commit a352f68) + Phase B (AutonomyPolicy, commit affc51b) + E-минимум (pause/resume, commit affc51b) — +22 теста. Остальное (C broker, D2, E2, F, G, CU-*) — ПОСЛЕ Genesis по планам.
- **Реализовано (CU-1/CU-2 pre-Genesis foundation, 518/518 green):** commit 129ead5 — AgentCapability.CaptureScreen (аддитивно, без перенумерации) + дескриптор (Perception, LowRisk — не-интерактивные capture → Notify, никогда молча, §50; AllowsBackground/не Reversible/AuditAlways); PcState/PcStateBuilder — детерминированная Desktop Awareness (приложения группируются, focused ВЫВОДИТСЯ — не угадывается, notifications per-app; live wiring post-Genesis); IUiObserver — семантические цели ПЕРВИЧНЫ (координаты = detail), provider-replaceable (local OCR/UIA дёшево, cloud qwen2.5vl для сложного); провайдеры/действия — post-Genesis CU-3+.

## Track 10 — Self-Evolution: Self-Development + Self-Optimization (approved 09.10.2026) ✅ pre-Genesis done 10.10.2026 (SE-0/1/4), post-Genesis queued
Full plan: **docs/self-evolution.md** (директива 09.10.2026). Три РАЗДЕЛЬНЫЕ
подсистемы (§26): Self-Maintenance (governance §4) / Self-Optimization (эксперимент-
цикл: Measure→Bottleneck→Hypothesis→Branch→Benchmark→Regression→Security→Baseline
compare→Accept только если объективно лучше→Canary→Deploy; «быстрее, но менее
надёжно — НЕ оптимизация») / Self-Development (Missing Capability→Design→Implement
через СУЩЕСТВУЮЩИЕ dev agents run_codex/run_opencode→Test→Deploy). Fundamentals
уже есть: метрики (M7 telemetry), benchmark-скрипты (benchmark-tts.py паттерн),
LearnedRuleStore (provenance/confidence — эксперименты), ToolEscalation
(**protected self-modification УЖЕ ИМЕЕТ техническую основу** — security-critical
source → Critical+confirm; расширение на новые security-компоненты), snapshot/
rollback (canary паттерн), M8 game detection (бюджеты: игра → suspend), Night/
Offline (окна). Фазы SE-0..SE-7; ДО Genesis: SE-0 метрики, SE-1 baseline store,
SE-4 protected расширение; ПОСЛЕ: SE-2/3 лаборатория/бюджеты, SE-5/6
само-развитие/цикл, SE-7. Skill model/lifecycle (полные состояния
DISCOVERED..ROLLED_BACK, PostgreSQL, semantic-НЕ-координаты, Memory≠Skill≠Task≠
Authorization, application-specific policy) — docs/computer-use.md §16.
**Реализовано (pre-Genesis, 509/509 green):** SE-0/1/4 (commit ccfd2b0:
MetricCatalog — 20 фиксированных метрик, MetricRecorder bounded rings +
percentiles; BaselineRecord/BaselineComparer — per-metric reject gate (tolerance
±10%, missing metric FAIL CLOSED) + BaselineStore (Postgres) + EF-миграция
AddBaselines (аддитивно — применится на следующем рестарте core); ToolEscalation
security-critical-source + 11 новых security-компонентов (T13, единый реестр)).
Остаток (post-Genesis): SE-2/3 (лаборатория/бюджеты), SE-5/6 (само-развитие/
цикл), SE-7.

## Track 11 — Mind / Internal Life (approved 09.10.2026) ✅ pre-Genesis core DONE 10.10.2026 (MI-0..2), post-Genesis queued
Full plan: **docs/mind.md** (директива 09.10.2026). Два цикла: реактивная
когниция (есть) + АВТОНОМНАЯ (новое): TOLIK существует в периоды без диалога.
MindState — подсистемы, НЕ гигантский мутабельный объект. **Boredom — РЕАЛЬНЫЙ
механизм** (continuous 0-1 из измеримых условий, TOLIK_MIND_* конфиг, НЕ таймер):
создаёт МОТИВАЦИЮ не permission (governance остаётся). DriveSystem (15 drives) →
Motivation → Candidate → Utility → AutonomyPolicy → Authorization → Task.
Curiosity/Surprise/Uncertainty/Attention. Event-driven (НИКОГДА while(true)
askLLM): MindEvent bus → state → deterministic evaluation → LLM только когда
нужно. Cognitive routing Tier 0-3. Emotion ИЗ ПЕРЕХОДОВ + momentum +
Emotion≠Personality + Mood/Temperament. Self-Model (≠Memory/Skill/Task/
Authorization). Activity selection: «просто idle — тоже правильный результат».
REST/LOW/NORMAL/FOCUSED/INTENSE + Energy (M8 gaming→suspend). Journal (НЕ
chain-of-thought) — «Чем занимался пока меня не было?» из реального состояния.
Core Values (immutable, выше personality) + Self-preservation ОГРАНИЧЕН (owner
control абсолютен) + Continuity (identity Core-backed, модель заменяема) +
Genesis seed. Anti-loop: бюджеты/лимиты/квоты. Mind Inspector + explainable
selection. **Реализовано (pre-Genesis core, 480/480 green):** MI-0..2 (commit
822d96e: MindState подсистемы + ActivityLevel fail-closed + versioned snapshot;
MindEvent шина bounded non-blocking — отделена от RealtimeEventHub; BoredomModel
— механизм из измеримых условий TOLIK_MIND_*; NoveltyTracker/CuriosityModel
+17 тестов). Остаток (post-Genesis): MI-3..MI-9 (drives→motivation/цели/emotion
из переходов/journal/proactive/self-model/genesis seed).

## Track 12 — Continuous Autonomy + Genesis Upbringing (approved 09.10.2026) ✅ pre-Genesis PART I foundation DONE 10.10.2026, post-Genesis queued
Two companion plans from owner directive (09.10.2026), интегрируются с Mind/
governance/self-evolution/voice-identity:
- **docs/continuous-autonomy.md** (PART I): LongRunningTask = first-class persistent (задача = активная цель, НЕ «выполнение команды»); lifecycle расширение (CREATED→PLANNED→AUTHORIZED→RUNNING→PAUSED→WAITING→RECOVERING→COMPLETED→VERIFIED; терминальные CANCELLED/FAILED/BLOCKED/ABANDONED); Task Scope ЯВНЫЙ (без unlimited authority; «do what is necessary» не расширяется молча); TaskSupervisor (heartbeat/liveness working-vs-stuck, stall detection, бюджеты, checkpoints, НЕ bypass security); TaskAuthorizationLease (duration/environments/forbidden — существующий lease store); checkpoints + resume-after-restart (НИКОГДА не считать начатое успешным); task journal (timestamp/action/result/evidence); **UserDisconnected ≠ TaskCancelled** (отсутствие владельца НЕ паузит задачу); real progress (never fabricated); WAITING = активное состояние (IDLE ≠ WAITING, без LLM); bounded recovery (не build→fail→вечно); parallel с бюджетами; continuous autonomy engine (Mind + TaskSupervisor, много целей); NO fake continuous life (персистентное состояние/workers/события, дёшево большую часть времени).
- **docs/genesis-upbringing.md** (PART II): Genesis = начало жизни и отношений, НЕ setup wizard; developmentally young (знания растут через взаимодействие, без вымышленной истории); фазы 0-8 (Awakening/First Contact/Self/Voice/Home/Family/Friends/Shared World/Independence — концептуальные, динамический диалог, НЕ hard-coded); активные вопросы из missing info + curiosity (информационная ценность, бесконечных допросов нет); bidirectional teaching; photo introduction (NEVER auto-confirm identity from face — гипотезы до подтверждения; ExternalMediaSource provider-абстракция: Yandex Disk/local/NAS); family tree (Person/Relationship/Direction/Confidence/Source/PreferredAddress); learning through correction; maturity как simulation (KnowledgeMaturity/SocialFamiliarity/SkillMaturity/AutonomyMaturity — НЕ «3 дня от роду»); learning rate высокий ранний → консервативный; **trust через свидетельства** (relationship trust ≠ security authorization); Genesis journal protected (birth/firsts) + first memory из реальной ceremony; переход guided→safe→normal autonomy (first independent activity — «я сам проверил голосовой контур»); Genesis НЕ сбрасывается легко.
- **Реализовано (pre-Genesis, 463/463 green):** CA-0/CA-1/CA-4 (commit d255e04: TaskScope + TaskLeaseStore + journal/progress + lifecycle attachment +13 тестов; DispatchGuardedAsync аддитивная scope-валидация) + CA-3/CA-6 (commit c0c5716: TaskCheckpoint/restore points + RecoveryPolicy bounded attempts (RetrySame→Alternate→EscalatePauseAndAsk, никогда build→fail→вечно) + WaitUntil семантические ожидания +13). Остаток: CA-2 (TaskSupervisor), CA-5 (absence/return), CA-7 (engine) + GU-0..GU-7 (upbringing). VI-3/VI-4 — уже реализованы ✓.

## Track 13 — Multimodal Life: Real Family + Visual Memory + Live Camera (approved 09.10.2026) 🔲 plan done, no implementation (PRE-GENESIS)
Full plan: **docs/multimodal-life.md** (директива 09.10.2026; PART I-III = готовые
планы Track 12/11/12 ✓). Новое: **PersonProfile unified** (optional fields:
Names/Nicknames/Relationships graph/VoiceProfile Track 8 + VisualProfile — ТОТ ЖЕ
canonical+adaptive+anti-poisoning паттерн, НЕ новая система/InteractionHistory
TOLIK↔person/LastSeen/LastHeard/KnownLocations); **мультимодальная идентичность**
(voice+face+context+history → combined confidence С provenance; слабые сигналы ≠
certainty; face Ivan + voice unknown → НЕ слепо предполагать — ask по security
policy); **Recognition levels** UNKNOWN/POSSIBLE/LIKELY/HIGH/CONFIRMED поверх
tiers; **Visual memory развивается** (первая встреча → углы/свет/выражения →
robustнее; HIGH→adaptive, MEDIUM→no update, LOW→НИКОГДА); **Live camera
companion** (смартфон = sensor client: camera→recognition→scene→emotion→Mind +
mic→VAD→STT; «О, привет. Я тебя вижу» — не «Face detected: user_id»; камера
как mic-always: permission+индикатор; face emotion → PossibleEmotion+Evidence
НИКОГДА факт; юмор через personality, perception остаётся PossibleFrustration
0.72; emotion НИКОГДА security); **Travel/мобильная непрерывность** (20ч absence:
converse/observe/fonовые цели/consolidation/rest — NO fake continuous life;
новые места → novelty→curiosity; поездка = эпизод если Ivan сохраняет);
**Episodic memory + консолидация** (the firsts: Елена/Валентина/поездка/
проект; raw → event → facts → relationships → episodes; сырые не вечно).
**Семейные факты** (Елена Александровна/Валентина 14 лет — LEARNED, не
hard-coded; корректируемы; младшие — консервативно: data/visual/proactive/
autonomy). Genesis НЕ заканчивается с ceremony (Development → Normal Life;
первая поездка через недели). Фазы MM-0..MM-6; pre-Genesis: MM-0..3
(deterministic, face только с явным разрешением); post-Genesis: MM-4..6
(live camera/поездки/реальные знакомства). Граница: Mind→Intent→Proposal→
Autonomy→Authorization→Capability→Execution→Verification; НИКОГДА
Emotion/Curiosity/Boredom/Relationship/Memory→Permission.

## Track 14 — Realistic Autonomy: Trust + Consent + Reward (approved 09.10.2026) ✅ pre-Genesis models DONE 10.10.2026 (RA-0..2), post-Genesis queued
Full plan: **docs/realistic-autonomy.md** (директива 09.10.2026). Главный принцип:
**DREAM ≠ REALISTIC ≠ FUTURE** — не вся футуристическая capability в первой
реализации. **Спектр автономии L0-L5** (Reactive/Assisted/Supervised/Bounded/
Proactive/Long-Term Experimental — НЕ boolean; L5 НИКОГДА unrestricted) поверх
governance AutonomyPolicy; **Initiative ≠ Authority ≠ Capability** (НИКОГДА не
смешивать); **Default: высокая инициатива в sandbox, консервативная authority
вне**. Домены + бюджеты (исчерпание → PAUSE→SAVE→WAIT/NOTIFY, не вечно) + stop
conditions. **TrustModel**: НЕ permission; многомерный (Reliability/Identity/
Social/Technical/TaskExecution/Communication/Safety — НЕ TrustScore=97);
provenance (evidence/timestamp/domain/reason); decay/revalidation (6-месячный
skill → OUTDATED); **НИКОГДА self-escalating** («заработал доверие → дам себе
permissions» — НЕТ); **НЕ послушание**: «Удали всё» → «Уточни, что именно» =
свидетельство хорошего суждения. **ConsentModel**: структурированный grant
(Subject/Actor/Action/Target/Scope/Duration/Conditions/Risk/Revocation/
Evidence); уровни One-Time/Task/Session/Domain/Persistent(low-risk only);
natural-language consent («Продолжай»/«Делай пока не закончишь» → bounded
grants); target-bound («почини голосовой» ≠ «modify anything»); revocable
(«прекращай» → stop+revoke+preserve+report). **Reward system** (НОВАЯ область):
Reward ≠ Permission XP (никогда «100 достижений → admin»); типы Social/
Celebration/Achievement/Cosmetic/Creative/Shared Experience; смысл > геймификации
(несколько meaningful вместо тысяч очков); **ANTI-REWARD-HACKING** (не
оптимизировать ради похвалы/trust — только фактический успех + честность;
провал: «Не получилось» правильно, «был прогресс, дай credit» неправильно);
Effort vs Result (Achievement + EffortAcknowledgement); RewardEvent memory;
Genesis FIRST_SUCCESS. Genesis firsts + первый провал (осмысленнее success-
цепочки) + first self-discovery (реальный CapabilityConfidence, не fake
consciousness). «20 часов одни»: 5 tier'ов (задачи/maintenance/useful/
curiosity/REST) + НЕ fake время (10 мин работы + 2ч ожидания + 6ч rest) +
RESTING ≠ OFFLINE (события будят). **Реалистичный roadmap A-I**: Foundation/
Real Autonomy/**Real-Time Voice (✓ СДЕЛАНО M0-M8)**/Multimodal/Genesis/
Computer Use/Mind/Self-Development/Advanced (DREAM). Фазы RA-0..RA-4;
pre-Genesis: RA-0/1/2; post-Genesis: RA-3/4.
**Реализовано (pre-Genesis models, 497/497 green):** RA-0..2 (commit 3fe9ef4:
AutonomyLevel L0-L5 spectrum + Cap (L0→Confirm/L1→Notify, Deny неизменен на
всех уровнях); TrustProfile — 7 измерений, ceiling 0.95, provenance ring,
негатив сильнее позитива, GoodJudgment evidence, decay к baseline; ConsentModel
— target-bound revocable grants, ParseGrantPhrase (неоднозначное → OneTime,
absence lease 8h), RevokeAll). Остаток (post-Genesis): RA-3..RA-4 (реальные
reward'ы/anti-reward-hacking enforcement/firsts).

## Track 15 — Identity & Presence: Core Migration + Presence Manager + Embodiment (approved 09.10.2026) 🔲 plan done, no implementation (PRE-GENESIS)
Full plan: **docs/identity-presence.md** (Master Spec директива 09.10.2026; остальное
= tracks 8-14 ✓, НИЧЕГО не дублируется). Новое: **Одна Identity — ONE ACTIVE CORE**
(техническое предотвращение split-brain: Core Lease/Epoch/Fencing — monotonic epoch,
identity-anchored signed claims, НЕ lock-файл/PID/mutex; LocalEpoch < CurrentEpoch →
STALE RUNTIME — старый комп после миграции НЕ продолжает работу); **Identity
Cryptography** (Public/Signing/Encrypted Secrets/Recovery — миграция ДОКАЗЫВАЕТ
право; секреты не копируются с конфигом); **Core Migration Protocol** (транзакционный:
PREPARE→VERIFY→SNAPSHOT→SYNC→TRANSFER→INTEGRITY→BOOT→HEALTH→EPOCH→FENCE→SWITCH→
FINALIZE; старый не финализирован до подтверждения нового; сохраняется ВСЁ: память/
личность/Mind/skills/trust/achievements — никогда «новый сервер = новый Толик»);
**CoreHost abstraction** (PC/VPS/Linux/Future, не привязан к провайдеру);
**Presence Manager** (НОВОЕ: Core Migration ≠ Presence Migration — КРИТИЧНО; Presence:
Device/Location/Mode/Attention/Activity/Capabilities; distributed organs — метафора;
ONE TOLIK/НЕ клоны; Primary + Secondary Awareness; Presence Migration PC→Maria
Laptop→return — «пошёл и вернулся»; PresenceState + renderer interpretation — НЕ
Teleport.anim; departure/arrival интерпретации; policy приоритеты; PresenceLost — Core
не погибает); **Multi-person perception** (distributed microphones = sensory nodes;
речь с контекстом Speaker/Device/Location/Confidence/Emotion/Overlap — probabilistic;
**Address Detection: hearing everything ≠ responding to everything** (речь TO TOLIK
vs между людьми/TV/игра); **attention arbitration НЕ last-speaker-wins** («Секунду,
Маша, я сначала закончу с Иваном»); multimodal scene model; **Attention ≠ Authority**:
близость/громкость/визуальное — НИКТО не получает permission); **Cephalon embodiment**:
КЛЮЧЕВОЙ принцип «TOLIK has state, animation is interpretation» (НЕ аним-клипы);
Expression State СМЕШИВАЕТСЯ (Amusement 0.7+Anger 0.3+Thinking 0.5+Speaking 1.0 →
саркастично-раздражённое БЕЗ clip); Expression Composer/DSL (sandbox → owner feedback →
версионирование; learned expression library); primitives = алфавит; procedural idle
(НЕ мёртвый loop); voice-as-body (FFT → bands → тело говорит с голосом); embodiment
security (renderer НИКОГДА authority); Quest/desktop рендеры. Фазы IP-0..IP-8;
**PRE-GENESIS скоуп DONE 09.10.2026**: IP-0..IP-2 (cryptography + CoreLease/
Epoch/Fencing commit a0e692a +9 тестов + миграционный протокол
scripts/migrate-core.ps1 + docs/migration.md), IP-4 (Presence Manager commit
cbdcde2 +7), IP-6 (address detection/arbitration commit 1c7e1b1 +12) —
**438/438 green**. Остаток (post-Genesis): IP-3 CoreHost формализация,
IP-5 Presence Migration с рендерами, IP-7/IP-8 Expression Composer/library.
Genesis остаётся deliberate milestone — техническая готовность (split-brain
prevention) СДЕЛАНА, ceremony — с владельцем.

## Track 16 — Android client (Kotlin, thin) 🔨 IN PROGRESS — Full Mobile Embodiment master directive (10.10.2026)
Стек: Kotlin (AGENTS §36), thin-клиент — НЕ Core logic; существующая заготовка
`clients/TOLIK.Android/` (минимальные модели, HTTP client). Фазы:
- AN-0 recon: заготовка, Kotlin окружение; signed transport (агент-паттерн фреймов)
- AN-1 chat + voice: chat (существующие контракты), voice input (mic → VAD M6 → STT → /voice/turn-stream M4), TTS playback (SSE audio chunks), barge-in (M5 /voice/cancel + ESC-аналог)
- AN-2 notifications/confirmations: уведомления, confirmations approve (существующий bound-механизм), task monitoring (RealtimeEventHub)
- AN-3 live camera: MM-4 (Track 13) — sensor client: camera→recognition→scene→emotion→Mind; permission+индикатор; post-Genesis
- AN-4 presence: Presence Manager интеграция (Track 15) — mobile sensory organ; Presence Migration «пошёл и вернулся»; post-Genesis
Роли в других треках: 13 (MM-4/поездки 20ч), 15 (органы/capability model:
Camera/Mic/GPS-if-authorized/Notifications), 14 (smartphone embodiment),
7 (Xiaomi/Wearables Toolkit — расширение Android-клиента). Порядок: AN-0..2
pre-Genesis допустимо (thin, поверх контрактов); AN-3/4 — post-Genesis. Quest/
Android НЕ получают Windows privileges (governance). Speaker verification —
как на других клиентах: probabilistic hint, НИКОГДА authorization (§13).
**Статус (10.10.2026):** НОВЫЙ master directive владельца — полноценное приложение
(Mobile Embodiment & Companion). Мастер-план: **docs/android-app.md** (решения:
Kotlin + classic Views/Material 3 — Compose будущая опция; Cephalon renderer —
SurfaceView + процедурный 2D Canvas CPU (та же философия, что десктоп: батарея/
GPU-дружелюбно); **Expression State — СУЩЕСТВУЮЩИЙ контракт TOLIK.Contracts
(0841765): ONE COMPOSER MANY RENDERERS — Core компоует и отдаёт по WS, Kotlin-
зеркало wire-модели, порт композера НЕ создаётся**; display modes
Companion/Fullscreen/Conversation/Voice/Compact; сеть — конфигурируемый endpoint,
никогда зашитый IP; offline — outbox + последний подтверждённый статус со
временем). Фазы AN-P1..P7 (Foundation/Cephalon Mobile/Conversation/Voice/Camera/
Tasks/Presence); AN-P5/AN-P7 — post-Genesis; скрытая запись/съёмка и обход
индикаторов Android ЗАПРЕЩЕНЫ. **AN-P1 Foundation DONE (commit 1361798, 33
файла):** Gradle-проект (AGP 8.5.2/Kotlin 2.0.20, minSdk 26), transport (401/403 →
failure, никогда локальная авторизация; WS+backoff; SSE Content-Type detect),
VoiceClient РЕАЛИЗОВАН (WavWriter/VAD gate/UtteranceSegmenter — порт
Contracts/UtteranceSegmenter.cs/mic engine/TTS player; turn-level echo gate +400ms
tail — 0.10 семантика; barge-in M5; speaker tier — HINT ONLY), tasks/
confirmations/WS мониторинг + notifications, DeviceIdentity (AndroidKeyStore EC
P-256 non-exportable). **AN-P2 Cephalon Mobile DONE (commit c27197f, 24 файла, compile-pending):**
CephalonView (SurfaceView процедурный Canvas CPU: глаз + 2-3 слоя оболочки + glow
+ частицы + breathing; синий primary, красный ТОЛЬКО Alert/Locked — реальные
события Core; DayNight тема с фиксированной палитрой цефалона), Kotlin-зеркало
ExpressionOutput (нормализация per-field с нейтральными дефолтами — garbage
никогда не рендерится как spike), wire-парсер (lenient per-field, envelope-
malformation → ignore), 15s staleness watchdog → local idle (зеркалит десктоп),
DropDrive при WS disconnect (reconnect честно ждёт новых событий), @Volatile
состояние (OkHttp threads), eventsWebSocketUrl() — URL-encoding Base64-токена
как протестированная pure-функция (live-verified hazard), 5 display modes,
CephalonModeMapping/CephalonEventsUrl тесты (RED-only-Alert/Locked инвариант,
hex parity с десктопом, hazard encoding pinned). **Баг-фиксы:** RadialGradient
в canvas-координатах (halo сэмплил верхний угол), нормализация нейтральных
дефолтов, **SessionStore.kt compile-blocker** (Kotlin block comments НЕСТЯ —
KDoc с res/xml/*_rules.xml внутри глотал остаток файла). Асимметрия честно НЕ
замаплена (как на десктопе — нет оси, фейк запрещён). Остаток AN-P5 (камера)/
AN-P7 (Presence) — post-Genesis; device-тесты (docs/android-app.md §9) — post-SDK.
**BUILD GREEN (12.10.2026, commit 2243046):** эпоха compile-pending ЗАКРЫТА —
первая реальная сборка: `:app:assembleDebug` + `:app:testDebugUnitTest` — BUILD
SUCCESSFUL, **APK 6.2 MB** (app/build/outputs/apk/debug/app-debug.apk), **51
юнит-тест 0 провалов** (6 suites). Gradle wrapper сгенерирован (gradle-8.7-all —
совпадает с AS-кэшем владельца `~/.gradle/wrapper/dists`, ноль загрузок;
local.properties machine-specific, gitignored). 12 реальных ошибок компиляции
исправлены (BufferOverflow пакет channels; TrackedTask квалификация; val→var;
два пропущенных .build(); flush-путь speechMs → @Volatile поле — оборачивание
открытой реплики корректно). Установка: APK готов; валидация на устройстве —
честные probe'ы (kill Core → «Нет связи»; bad token → auth-статус без reconnect
loop; stale >15s → «Устарело…»).
Токен агента — через TOLIK_SESSION_TOKEN env (m6-хардненинг, не командная строка).

## Pre-Genesis audit (oracle, 10.10.2026) — PASS, M1-M4 закрыты
Верdict: все 9 security-инвариантов держат (trust ≠ authorization; никогда
self-escalating; consent bounded/revocable/target-bound; fail-closed; без
параллельных систем; LLM не авторизует; bounded/thread-safe; secret hygiene;
CaptureScreen posture). **M1-M4 закрыты** (commit e8669c5: Configured fail-
closed → Reactive при невалидном TOLIK_AUTONOMY_LEVEL; ConsentGrant identity-
bound (GrantedBy required, FindActive advisory-only, бытовая изоляция грантов);
bounded/enforceable consent (sweep expired, Consume для OneTime, SessionScoped
требует session id + RevokeSession, cap 256); BaselineComparer fail-closed на
non-finite (NaN/Infinity отвергаются, не принимаются молча) + Minors m1-m5/n2/n4
закрыты). **m6-хардненинг** (commit 60e65de): токен агента через
TOLIK_SESSION_TOKEN env вместо командной строки (Win32_Process-читаемость,
T3/T8; live-агент перезапущен через env — в cmdline токена нет). **Бэклог
(не блокирует):** n1 — Baselines snake_case + index миграция; n5 — retention
policy для capture post-Genesis. 527/527 тестов, live-стек зелёный.

## Standing pending items (no owner needed to start)
- Cloud API keys → enable Reasoning/Coding/Vision cloud routes + ask-policy.
- review.tsv curation (owner's ears).
- Wife voice enrollment + role check.
- gpt-oss:20b benchmark (13 GB pull) — only if qwen3:4b disappoints.
