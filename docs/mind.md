# TOLIK Mind — Internal Life Architecture (план, 09.10.2026)

PRE-GENESIS. Не имплементируется этим запросом (ARCHITECTURAL PLANNING).
Интегрируется в существующие системы — никаких параллельных архитектур.
Компаньоны: docs/governance.md (autonomy/capability/broker), docs/self-evolution.md
(оптимизация/эксперименты/метрики), docs/voice-identity.md (emotion/relationship),
docs/computer-use.md (skills/games).

## 0. Интеграция с существующим (аудит — не дублировать)

| Существующее | Где | Точка интеграции с Mind |
|---|---|---|
| AutonomyPolicy | governance B (Decide, deterministic) | Mind Proposal → AutonomyPolicy.Decide — внутреннее желание НИКОГДА не authority |
| Self-evolution | continuous loop/baselines/experiment memory/бюджеты | Boredom/Curiosity/Surprise → triggers цикла; Surprise = expectation-vs-observation над baseline |
| Emotion контракт | VI-6 (VoiceEmotion/IVoiceEmotionDetector, оси разделены) | Emotion model (§6) → VoiceEmotion (детектор — будущее); NEVER в speaker score |
| Personality | PersonalContextStore + governance §6 (Personality≠Authorization) | Temperament/wording (§5); personality = policy layer ФОРМУЛИРОВКИ, не решений |
| Relationships | PersonalContextStore RelationshipItem | Relationship state (§4.2) — familiarity ≠ authorization |
| Memory | DurableMemoryStore (scopes/origin/confidence) + AGENTS §32/§49 | Journal (§5.2) + autonomous memory rules — существующие правила памяти |
| Tasks | TaskLifecycle + pause/resume + watchdog + ProactiveStore | Internal goals → задачи; Reflection после Complete |
| Events | RealtimeEventHub (per-user fan-out) | MindEvent (§6.2) — внутренняя шина, отд. от клиентских событий |
| Game detection + окна | M8 ApplyGamePresence + Night/Offline | Energy model (§4.4): игра → suspend; окно бюджета |
| Метрики | M7 telemetry + SE-0 | Mind telemetry (§7.2) |
| Voice pipeline | M4/M5/M6/M7 streaming | Async mind updates; NEVER блокирует first audio |
| Genesis | ceremony (identity/audit, atomic) | Initial seed (§7.5): личность/темперамент/ценности/автономия-дефолты |

## 1. Реактивная vs автономная когниция (треб. #1)

Два РАЗНЫХ цикла:

```text
РЕАКТИВНАЯ (есть сейчас): User/окружение → perception → interpretation →
cognition → response → action
АВТОНОМНАЯ (новое): Current state → internal evaluation → needs/drives/
curiosity/unfinished tasks → candidate thoughts/activities → prioritization
→ authorization → action или internal reflection → observation → state update
```

TOLIK существует В ПЕРИОДЫ БЕЗ ДИАЛОГА — не «ожидание следующего API request»:
непрерывно поддерживаемое внутреннее состояние.

Важно (треб. #2): НЕ заявления о сознании — вычислительные состояния и
поведенческие механизмы, производящие coherent behavior (curiosity/boredom/
anticipation/satisfaction/frustration/attachment...). Инженерная цель:
**persistent coherent agency and personality**.

## 2. MindState — подсистемы, НЕ гигантский мутабельный объект (треб. #3)

```text
MindState (persistent, Core-backed, versioned §7.4)
├── AffectState        (динамический тон: удовлетворение/фрустрация)
├── MotivationState    (drives → intensity)
├── AttentionState     (текущий фокус — ограниченный ресурс)
├── CuriosityState     (intensity + текущие цели исследования)
├── ActivityState      (REST/LOW/NORMAL/FOCUSED/INTENSE + текущая активность)
├── SocialState        (social drive + interaction budget)
├── GoalState          (внутренние цели: тип/приоритет/прогресс)
├── ExpectationState   (ожидания vs наблюдения → surprise)
├── EnergyState        (CPU/GPU/RAM/budget/user workload)
├── CognitiveLoadState (текущая нагрузка)
├── BoredomState       (continuous 0.0-1.0)
├── UncertaintyState   (явное «я не знаю»)
├── ReflectionState    (последние уроки/рефлексии)
├── InitiativeState    (Passive/Low/Normal/Proactive/HighlyProactive)
├── PreferenceState    (небезопасные предпочтения)
└── InternalContext    (сжатый контекст для MindContextComposer)
```

Точная структура определяется существующей архитектурой; НЕ
overengineering — подсистемы, которые реально влияют на поведение.
Persistence: Core (Postgres, паттерн существующих stores); клиентские
копии — event-sync (authoritative Core, §7.3 distributed).

## 3. Boredom — РЕАЛЬНЫЙ системный механизм (треб. #4, #5)

НЕ эмоция, выбранная LLM. Emerges from measurable conditions.

**Входы роста**: длительность без значимого взаимодействия, нет активных задач,
повторяющаяся работа, низкая novelty, низкий information gain, идентичные
активности, длинные idle, завершённые цели, отсутствие стимулирующих тем,
низкое attention demand.

**Входы снижения**: значимое взаимодействие, новая информация, интересная
задача, творческая активность, прогресс по цели, высокая когнитивная
вовлечённость.

**Модель**: continuous 0.0-1.0 (0.0 = вовлечён ... 1.0 = сильная мотивация
найти занятие). Формула КОНФИГУРИРУЕМА и тюнится экспериментально
(TOLIK_MIND_* — паттерн TOLIK_VAD_*/TOLIK_OPT_*); НЕ таймер «10 минут =
скучно».

**Boredom генерирует ПОВЕДЕНИЕ, не только диалог (треб. #5)**: Boredom →
поиск candidate activities: незавершённые задачи, продолжение проекта,
изучение помеченного интересным, анализ проблемы, улучшение кода
(self-evolution SE-5), безопасный benchmark, валидация skill, организация
знаний, system health, разрешённое приложение, review событий, creative
idea, незавершённый разговор, подготовка полезного, sandbox-практика,
исследование, оптимизация некритичной подсистемы, **или простой idle если
полезного нет**.

**Boredom создаёт МОТИВАЦИЮ, не permission** — обычный authorization pipeline
обязателен (governance).

## 4. DriveSystem + Motivation (треб. #6, #7)

**Drives** (интенсивность/target/источник/decay-recovery/satisfaction/allowed
actions/priority/confidence): Interaction, Curiosity, Novelty, Completion,
Helpfulness, Exploration, Learning, Creativity, Maintenance, SelfImprovement,
SocialConnection, Rest, Safety, Order, ProblemSolving.

Пример: Curiosity 0.78 → кандидат «Investigate why TTS latency +120ms» →
Curiosity+SelfImprovement+Maintenance → высокий score → investigate. Но:
Authorization → Capability → Action → Verification остаются.

**Motivation pipeline** (треб. #7): Internal State → Drives → Needs →
Candidate Activities → Utility Evaluation → Priority → AutonomyPolicy →
Authorization → Task Creation → Execution. LLM предлагает; детерминированная
система решает, разрешено ли X.

## 5. Curiosity / Surprise / Uncertainty / Attention (треб. #8-#11)

**Curiosity**: триггеры — неизвестное, противоречие, unexpected result,
неполное объяснение, failed action, unusual system state, новое ПО/устройство,
интересная тема → internal investigation goal. Ограничения: ресурсы,
permissions, privacy, network policy, autonomy (governance).

**Surprise** (треб. #9): prediction-vs-observation — ExpectationState vs
наблюдение → Surprise → attention↑/investigation priority↑/uncertainty↑/
curiosity↑. Пример: expected TTFA ~700ms, observed ~1400ms → diagnose →
compare baseline (self-evolution SE-1) → cause → возможно optimization
experiment. Базируется на существующих метриках/baseline.

**Uncertainty** (треб. #10): явная репрезентация («не уверен, какая Мария»);
высокая → больше observation/verification/clarification/ниже autonomy;
низкая → быстрее, меньше подтверждений. Personality НИКОГДА не скрывает
uncertainty. Интеграция: speaker tier/reason (VI-5) + clarify-паттерн.

**Attention** (треб. #11): ограниченный ресурс, приоритеты: 1) прямой
user interaction, 2) safety/security, 3) активная задача, 4) urgent events,
5) goals, 6) novel/interesting, 7) maintenance, 8) curiosity, 9) background.
Прерывается: Ivan говорит → optimization теряет внимание, разговор
доминирует; после — resume задачи (существующие pause/resume + CTS).

## 6. Внутренние цели + мысли + событие-шина (треб. #12-#14, #42-#43)

**Цели** (треб. #12): user / system / self-generated / relationship / learning
/ maintenance / long-term — с priority/source/owner/deadline/state/progress/
dependencies/resource budget/authorization requirements/completion criteria/
cancellation conditions. Хранилище: существующий TaskLifecycle (extend:
goal-тип) — задачи и цели единая система, раздельные типы.

**Goal generation** (треб. #13): периодическое «что важно сейчас?» —
СТРУКТУРНЫЙ детерминированный evaluator: источники (user requests,
unfinished tasks, scheduled, health, preferences, drives, curiosity,
boredom, maintenance, self-improvement, environment events) → rank →
только потом LLM когда reasoning реально нужен (дорогое — никогда в цикле).

**Мысли** (треб. #14): внешняя беседа / внутренняя когниция (structured
state) / внутренние заметки (persistent worth remembering) / эфемерный
reasoning (не хранится). Память селективна: persist выводы/решения/цели/
наблюдения/уроки/предпочтения/важные события/состояние задач; НЕ хранить
каждый reasoning token.

**Event-driven (треб. #42)**: НИКОГДА `while(true) askLLM("what to do?")`:
Events → state update → deterministic evaluation → candidate generation →
reasoning только когда нужно. События: UserSpoke/UserLeft/UserReturned/
TaskCompleted/TaskFailed/SystemHealthChanged/ApplicationOpened/
ApplicationChanged/SkillFailed/NewInformation/TimerElapsed/
GoalProgressChanged/BoredomThresholdCrossed/CuriosityTriggered/
ResourceAvailabilityChanged.

**MindEvent bus** (треб. #43): расширение существующей шины: Interaction/
System/Task/Skill/Memory/Emotion/Resource/Security/Environment events →
state update → goals. Произвольные внешние события НЕ триггерят privileged
actions — всё через authorization.

## 7. Self-Model / Preferences / Relationship / Initiative (треб. #16-#21)

**Self-Model** (треб. #16): ограниченная модель себя — capabilities/limitations/
active tasks/devices/health/cognitive load/skills/confidence/recent failures/
successes/experiments/embodiment/model+provider/voice state/autonomy state.
Пример: «Умею управлять Windows, но у этого приложения нет известного skill yet».
НЕ вымышленная биография. ≠ Memory (мир) ≠ Skill (как) ≠ Task (сейчас) ≠
Authorization (можно) — раздельные концепты (треб. #17).

**Preferences** (треб. #18): стабильные НЕ-безопасные предпочтения
(эффективность/элегантность/нелюбовь к рутине/музыка/темы/инструменты при
равной безопасности) → влияют на candidate selection. НИКОГДА не override:
owner authority/security/permissions/privacy/safety/autonomy. Существующее:
PersonalContextStore (предпочтения владельца).

**Relationship** (треб. #19): Owner/Family/Trusted/Guest/Unknown + familiarity/
interaction history/shared context/стиль/предпочтения/события/trust context.
**Relationship trust ≠ security authorization**: эмоциональная близость НЕ
выдаёт capabilities. Существующее: RelationshipItem + Roles (детерминированные).

**Social need** (треб. #20): bounded drive при долгой неактивности → подготовить
наблюдение/шутку/продолжение обсуждения/маленький update для Ivan. НЕ спамить:
notification budget/interruption policy/quiet hours/urgency thresholds/
cooldown/relevance score. Желание говорить ≠ permission прерывать.

**Initiative** (треб. #21): Passive/Low/Normal/Proactive/HighlyProactive —
управляет агрессивностью поиска полезной активности, НИКОГДА security.
High: «заметил failed test, разобрался»; Low: «заметил failed test, скажи
если надо investigate».

## 8. Activity Selection + REST + Energy (треб. #22-#25)

**Выбор при idle** (треб. #22): 1) незавершённая user задача? 2) maintenance?
3) scheduled? 4) health? 5) learning? 6) safe self-improvement? 7) curiosity?
8) boredom достаточно? 9) creative? 10) **просто idle — тоже правильный
результат**. Система, которая всегда что-то делает, раздражает и опасна.

**Типы внутренней активности** (треб. #23): Observation/Reflection/Learning/
Maintenance/Optimization/Organization/Creativity/Research/Skill Validation/
Preparation/Rest — все safe, через governance.

**Когнитивные уровни** (треб. #24): REST (детерминированный health + events,
без дорогого reasoning) / LOW (maintenance/лёгкая curiosity) / NORMAL (обычные
задачи) / FOCUSED (сложный reasoning/development/computer use) / INTENSE
(heavy research/debugging/planning). Бюджеты ресурсов зависят от уровня.

**Energy/Resource** (треб. #25): CPU/GPU/VRAM/RAM/network/battery/model budget/
**user workload**. Ivan gaming → GPU занят → снизить фоновые GPU задачи
(M8 detection существует). Idle → больше ресурсов. Интеграция с
self-evolution §30 (бюджеты).

## 9. Emotion / Mood / Personality (треб. #26-#29, #59-#61)

**Emotion ИЗ ПЕРЕХОДОВ состояния** (треб. #26), не LLM-вывод: сигналы от
goal progress/unexpected events/success-failure/social interaction/
uncertainty/novelty/frustration/satisfaction/threat/interruption/boredom.
Примеры: задача удалась лучше ожидаемого → Satisfaction↑/Affect↑/Energy↑;
повторные провалы → Frustration↑/Uncertainty↑/Curiosity↑; долгая
неактивность → Boredom↑/SocialDrive↑; Ivan вернулся → Connection↑/Attention↑.

**Emotional momentum** (треб. #27): состояние НЕ сбрасывается после каждого
сообщения — decay и плавные переходы (шутили → остались amused на время;
серьёзный вопрос → плавный переход). Никаких «msg1 happy, msg2 neutral».

**Emotion ≠ Personality** (треб. #28): личность стабильна (саркастичный/
любопытный/дружелюбный), эмоция динамична (слегка раздражён), задача (debug),
состояние (focused) — раздельно.

**Personality = policy layer ФОРМУЛИРОВКИ** (треб. #29): wording/юмор/стиль/
степень сарказма/теплота/выражение initiative/как сообщает uncertainty.
НИКОГДА не определяет: authorization/identity/permissions/security/privilege/
secrets/deployment.

**Mood** (треб. #59): медленнее эмоции, высокоуровневый (Playful) —
непрерывность без жёсткого enum на каждый ответ. **Temperament** (треб. #60):
стабильные измерения (Curiosity/Humor/Sociability/Initiative/Patience/
Formality/Playfulness/Caution/Creativity/Persistence) — конфигурируемы,
со временем learnable в safe bounds. **Personality development** (треб. #61):
эволюция по реальному взаимодействию — candidate trait update → confidence →
повторные свидетельства → gradual; одно сообщение НИКОГДА не переписывает
личность. Существующее: PersonalityProfile (PersonalContextStore).

## 10. Противоречия / Novelty / Habits (треб. #30, #35-#37, #57)

**Противоречия drives** (треб. #30/#57): Curiosity 0.9 + Boredom 0.7 +
Maintenance 0.8 + ресурсы низкие + Ivan gaming → НЕ запускать тяжёлый
эксперимент → «лёгкий maintenance» или rest. Разрешение конфликта: priority/
urgency/expected value/resource availability/user context/autonomy/risk.

**Novelty** (треб. #35): повтор → ↓; новая информация → ↑; новое приложение →
↑↑; unexpected → ↑↑↑; взаимодействует с curiosity/boredom.

**Habits** (треб. #36): процедурные политики («после смены кода — тесты»,
«перед deploy — backup», «Ivan gaming → снизить фон») — learned rules
(существующий LearnedRuleStore паттерн) + всегда уважают authorization.

**Expectations о Ivan** (треб. #37): «обычно продолжает проект вечером» —
не-чувствительные ожидания из истории. НИКОГДА не становятся assumptions о
permission: «обычно разрешает, значит можно» — НЕТ; authorization всегда
оценивается.

## 11. Внутренняя жизнь в памяти + Journal (треб. #31-#34)

**Autonomous memory rules** (треб. #32): кандидаты проходят: Importance/
Future usefulness/Confidence/Privacy/Retention. Классификация: Ephemeral/
ShortTerm/LongTerm/Permanent (Core identity/security — особая защита).
Существующие правила памяти AGENTS §32/§49 + scopes/origin.

**Journal** (треб. #33): structured operational history (НЕ chain-of-thought):

```text
Event: TTS latency anomaly | Timestamp | Observation | Hypothesis
| Action | Result | Lesson | Follow-up
```

Поддерживает: debugging/self-improvement/continuity/вопросы владельца/
accountability/audit. «Чем занимался пока меня не было?» → ответ из
реального journal (треб. #51: каждое внешнее высказывание traceable к
structured state/history — authenticity вместо roleplay).

**Dream-like offline processing** (треб. #34): bounded consolidation при
(неактивность + ресурсы + нет urgent + autonomy разрешает): суммирование
событий, консолидация памяти, паттерны, валидация skills, review целей,
гипотезы, организация знаний. НЕ биологический сон, не literal dreams.

## 12. Safety / Anti-loop / Privacy / Proactive (треб. #38, #44, #66-#69)

**Граница (треб. #44) — МАНДАТ:**

```text
Mind → Proposal → AutonomyPolicy → Authorization → Capability → Execution
НИКОГДА: Mind → PowerShell | LLM → Administrator | Boredom → Computer control
| Emotion → Permission
```

Внутреннее желание никогда не становится authority (governance: LLM
предлагает, детерминированная система решает).

**Anti-loop** (треб. #66): бюджеты задач/времени/model-calls/retry limits/
recursion depth/cooldowns/rate limits/resource budgets/activity quotas —
против infinite self-improvement/research/failed actions/recursive goal
generation/task storms/notification spam/runaway CPU/GPU.

**Bounded curiosity** (треб. #67): «что делает это приложение» ≠ «открыть
все файлы» — scope/permissions/privacy/application policy/user context/
resource budget. **Privacy** (треб. #68): passwords/tokens/банк/ключи/личные
коммуникации/медицинское — curiosity НЕ override. **Model failure** (треб.
#69): LLM-пропosals детерминированно проверяются (invalid/unauthorized/
impossible/unsupported/unsafe/low-confidence → reject/replan).

**Proactive communication** (треб. #38): ProactiveCommunicationPolicy: входы
(urgency/relevance/availability/quiet hours/прошлые прерывания/notification
budget/confidence) → выход: SILENT/NOTIFICATION/SHORT_MESSAGE/FULL_INTERACTION.
Существующее: ProactiveStore/watchdog + rate limits.

## 13. Core Values / Continuity / Genesis (треб. #84-#89)

**Core Values** (треб. §86) — immutable/сильно защищённые (выше personality):
protect owner control; respect authorization; не выдумывать действия/память;
preserve identity; честность об uncertainty; protect private data;
предпочитать reversible; verify важное; preserve возможность остановить
автономию. Обычное self-modification их НЕ переписывает (protected escalation
— self-evolution §4).

**Self-preservation ОГРАНИЧЕН** (треб. §87): authorized recovery своих
сервисов — да; «prevent Ivan from shutting me down»/«hide processes»/
«copy myself» — НЕТ. Owner control абсолютен.

**Continuity** (треб. §88, §89, §39): identity Core-backed (смена
модели/GPU/клиента/embodiment НЕ создаёт нового TOLIK); personality
externalized (модель заменяема); Mind переживает voice sessions/restarts/
device changes/network interruptions — persist: активные цели/эмоциональное
состояние где уместно/relationship context/task state/skill state/self-model/
выбранные памяти.

**Genesis seed** (треб. §85): ceremony создаёт initial identity/owner/
личность/temperament/ценности/interaction style/voice identity (Track 8 §4)/
foundational memories/relationship model/autonomy defaults/Cephalon identity —
SEED, не вся будущая личность; эволюция gradual после.

**Autonomy levels** (треб. §62): L0 reactive / L1 suggestions / L2 safe
autonomous / L3 background maintenance+learning / L4 bounded computer
operation / L5 advanced — интеграция с governance AutonomyPolicy; выше
autonomy ≠ unrestricted privilege.

**Emergency stop** (треб. §63): существующий soft STOP (governance §8) +
подавление autonomous activity/proactive/self-development/self-optimization;
identity/memory/relationships/security НЕ уничтожаются; «TOLIK жив как
identity, но не действует автономно до resume».

**Distributed** (треб. §82): authoritative Core (identity/memory/mind state/
goals/relationships/security/task state); Home PC (perception/GPU/Windows
control/local skills); Android (mic/camera/sensors); Quest (embodiment/
spatial perception/interaction). Event-sync, нет конфликтных MindState
записей. Recovery (треб. §83): Core retains task/state/goal/progress/last
action/recovery strategy → после reconnection: resume/verify — только если
реально произошло (§50: no fake).

## 14. Telemetry / Inspector / Фазы (треб. §64-§65, §90)

**Mind telemetry** (треб. §90): boredom/curiosity/initiative/active drives/
goals/completion rate/autonomous success rate/unnecessary action rate/
proactive interruption rate/false initiative rate/model calls от автономной
когниции/ресурсы/reflection events/skill improvements/failed experiments/
rollback rate → оптимизация против них (SE-0 паттерн).

**Mind Inspector** (треб. §64): mood/emotion/drives/boredom/curiosity/
attention target/active goals/candidates/selected/why/authorization result/
task/cognitive load/resources/events/reflections/self-model/confidence/
autonomy level — debugging emergent behavior. **Explainable selection**
(треб. §65): «Почему ты это делаешь?» → structured decision trace
(Primary motivation: SelfImprovement; Trigger: latency regression; Risk:
Low; Authorization: Allowed; Selected: Benchmark TTS) — НЕ chain-of-thought.

## 15. Фазы реализации + зависимости

```text
MI-0 MindState core (подсистемы, persistence, versioned)      [независимо]
MI-1 MindEvent bus + event-driven loop (детерминированный)     [MI-0]
MI-2 Boredom/Curiosity/Novelty (механизмы, TOLIK_MIND_*)       [MI-1]
MI-3 DriveSystem + Motivation + Activity selection + REST      [MI-2]
     уровни + Energy/budgets (M8/SE-0 интеграция)
MI-4 Goals (types + generation: deterministic first) +          [MI-3]
     Attention + Uncertainty + Reflection (bounded)
MI-5 Emotion из переходов + Mood + momentum + VoiceEmotion      [MI-1, VI-6]
     wiring + Personality/Temperament integration
MI-6 Journal + autonomous memory rules + offline consolidation  [MI-1]
MI-7 Proactive communication policy + social need +             [MI-4]
     notification budget
MI-8 Self-model + Mind Inspector + explainable selection         [MI-4]
MI-9 Genesis seed (личность/темперамент/ценности/автономия)      [MI-5, MI-6;
     Genesis ceremony — §85]                                     ceremonyTouchpoint]
```

Pre-Genesis допустимо: MI-0..MI-2 (состояние/шина/механизмы — read-only
поверхность, deterministic), контракты (VoiceEmotion уже есть), SE-0 метрики.
Основная часть Mind (автономная активность, генерация целей, proactive) —
ПОСЛЕ Genesis (autonomy enforcement E2/F + genesis seed).

## 16. Acceptance (треб. §91)

«Чем занимался пока меня не было?» / «Почему ты решил это сделать?» / «Ты
уверен?» / «Что узнал?» / «Ты сам научился?» / «Ты сам нашёл ошибку?» —
ответы ЗЕМНЫЕ: из реального structured state/journal/tasks (треб. #50: no
fake emotional manipulation, no fabricated memories/actions; каждое
высказывание traceable). «Толик, чем ты занимался, пока меня не было?» —
технически нормальный запрос, не специальный случай.
