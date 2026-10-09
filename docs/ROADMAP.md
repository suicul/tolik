# TOLIK Master Plan (30.09.2026)

Living plan: status + next actions per track. VPS migration is distant-future
by owner decision — not on the active list.

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

## Track 3 — Native Windows UI (WPF, approved) 🔲 not started — BLOCKED on provider
Cephalon overlay: transparent always-on-top, idle/listening/thinking/speaking/
working/denied states, direct mic (no browser), mic-always-on WITH indicator
(approved), game mode silent. Web UI stays as fallback. Voice-ID enroll wizard exists in Web UI (3 phrases +
verify); port it. Needs: wake-word (openWakeWord) integration,
challenge-response for sensitive voice actions.
**BLOCKER (04.10.2026):** три подряд stall'а designer-линии на этой задаче
(две сессии, ноль файловых записей — провайдер системно убивает длинные поколения
на этом окружении). Первый MVP-slice размечен (skeleton → overlay → state machine
→ tray, mic — позже); попытка возобновления — после стабилизации провайдера
(или интерактивный режим).

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

## Track 10 — Self-Evolution: Self-Development + Self-Optimization (approved 09.10.2026) 🔲 plan done, no implementation (PRE-GENESIS)
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

## Track 11 — Mind / Internal Life (approved 09.10.2026) 🔲 plan done, no implementation (PRE-GENESIS)
Full plan: **docs/mind.md** (директива 09.10.2026). Два цикла: реактивная
когниция (есть) + АВТОНОМНАЯ (новое): TOLIK существует в периоды без диалога.
MindState — подсистемы (Affect/Motivation/Attention/Curiosity/Activity/Social/
Goal/Expectation/Energy/Boredom/Uncertainty/Reflection/Initiative), НЕ гигантский
мутабельный объект. **Boredom — РЕАЛЬНЫЙ механизм** (continuous 0-1 из
измеримых условий, TOLIK_MIND_* конфиг, НЕ таймер): создаёт МОТИВАЦИЮ не
permission (governance остаётся). DriveSystem (15 drives) → Motivation →
Candidate → Utility → AutonomyPolicy → Authorization → Task. Curiosity/Surprise
(expectation-vs-observation над baseline SE-1)/Uncertainty (явное «не знаю»)/
Attention (ограниченный ресурс, прерывается разговором). Event-driven (НИКОГДА
while(true) askLLM): MindEvent bus → state → deterministic evaluation → LLM
только когда нужно. Cognitive routing Tier 0-3 (deterministic/tiny/normal/
expensive). Emotion ИЗ ПЕРЕХОДОВ (не LLM-вывод) + momentum + Emotion≠Personality
+ Mood/Temperament (PersonalContextStore). Self-Model (≠Memory/Skill/Task/
Authorization). Activity selection: «просто idle — тоже правильный результат».
REST/LOW/NORMAL/FOCUSED/INTENSE + Energy (M8 gaming→suspend). Journal
(structured operational history, НЕ chain-of-thought) — «Чем занимался пока
меня не было?» из реального состояния. Core Values (immutable, выше personality)
+ Self-preservation ОГРАНИЧЕН (owner control абсолютен) + Continuity (identity
Core-backed, модель заменяема) + Genesis seed. Anti-loop: бюджеты/лимиты/квоты.
Mind Inspector + explainable selection (structured decision trace, не CoT).
Фазы MI-0..MI-9; pre-Genesis: MI-0..2 (deterministic, read-only) + SE-0;
основное (автономная активность/proactive/генерация целей) — после Genesis.

## Track 12 — Continuous Autonomy + Genesis Upbringing (approved 09.10.2026) 🔲 plan done, no implementation (PRE-GENESIS)
Two companion plans from owner directive (09.10.2026), интегрируются с Mind/
governance/self-evolution/voice-identity:
- **docs/continuous-autonomy.md** (PART I): LongRunningTask = first-class persistent (задача = активная цель, НЕ «выполнение команды»); lifecycle расширение (CREATED→PLANNED→AUTHORIZED→RUNNING→PAUSED→WAITING→RECOVERING→COMPLETED→VERIFIED; терминальные CANCELLED/FAILED/BLOCKED/ABANDONED); Task Scope ЯВНЫЙ (без unlimited authority; «do what is necessary» не расширяется молча); TaskSupervisor (heartbeat/liveness working-vs-stuck, stall detection, бюджеты, checkpoints, НЕ bypass security); TaskAuthorizationLease (duration/environments/forbidden — существующий lease store); checkpoints + resume-after-restart (НИКОГДА не считать начатое успешным); task journal (timestamp/action/result/evidence); **UserDisconnected ≠ TaskCancelled** (отсутствие владельца НЕ паузит задачу); real progress (never fabricated); WAITING = активное состояние (IDLE ≠ WAITING, без LLM); bounded recovery (не build→fail→вечно); parallel с бюджетами; continuous autonomy engine (Mind + TaskSupervisor, много целей); NO fake continuous life (персистентное состояние/workers/события, дёшево большую часть времени).
- **docs/genesis-upbringing.md** (PART II): Genesis = начало жизни и отношений, НЕ setup wizard; developmentally young (знания растут через взаимодействие, без вымышленной истории); фазы 0-8 (Awakening/First Contact/Self/Voice/Home/Family/Friends/Shared World/Independence — концептуальные, динамический диалог, НЕ hard-coded); активные вопросы из missing info + curiosity (информационная ценность, бесконечных допросов нет); bidirectional teaching; photo introduction (NEVER auto-confirm identity from face — гипотезы до подтверждения; ExternalMediaSource provider-абстракция: Yandex Disk/local/NAS); family tree (Person/Relationship/Direction/Confidence/Source/PreferredAddress); learning through correction; maturity как simulation (KnowledgeMaturity/SocialFamiliarity/SkillMaturity/AutonomyMaturity — НЕ «3 дня от роду»); learning rate высокий ранний → консервативный; **trust через свидетельства** (relationship trust ≠ security authorization); Genesis journal protected (birth/firsts) + first memory из реальной ceremony; переход guided→safe→normal autonomy (first independent activity — «я сам проверил голосовой контур»); Genesis НЕ сбрасывается легко.
- Фазы CA-0..CA-7 + GU-0..GU-7; PRE-Genesis: CA-0/CA-1/CA-3/4-минимум/CA-6 + GU-0/GU-2/GU-3 (deterministic поверх существующих); ПОСЛЕ Genesis: CA-5/CA-7 + GU-4..GU-7. VI-3/VI-4 (voice/conversational enrollment) — уже реализованы ✓.

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

## Track 14 — Realistic Autonomy: Trust + Consent + Reward (approved 09.10.2026) 🔲 plan done, no implementation (PRE-GENESIS)
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
**IP-0..IP-2 DONE 09.10.2026** (cryptography формализация + CoreLease/Epoch/Fencing
реализовано commit a0e692a +9 тестов 419 green + миграционный протокол
scripts/migrate-core.ps1 + docs/migration.md — abort-on-fail, -WhatIfMode,
скрипт не убивает процессы/не пишет DB; docker-верификация — выходные владельца);
pre-Genesis остаток: IP-4/IP-6 (детерминированные); post-Genesis: IP-5/IP-7/IP-8.
Genesis остаётся deliberate milestone — техническая готовность сначала, ceremony
потом.

## Standing pending items (no owner needed to start)
- Cloud API keys → enable Reasoning/Coding/Vision cloud routes + ask-policy.
- review.tsv curation (owner's ears).
- Wife voice enrollment + role check.
- gpt-oss:20b benchmark (13 GB pull) — only if qwen3:4b disappoints.
