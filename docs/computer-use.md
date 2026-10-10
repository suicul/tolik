# TOLIK Computer Use — GUI Automation, Skill Learning (план, 08.10.2026)

PRE-GENESIS. Не имплементируется этим запросом. Аудит по факту кода (exp-9).
Компаньон: docs/governance.md (capability registry, autonomy policy, broker —
исполнение Computer Use идёт ЧЕРЕЗ них, никаких параллельных систем).

## 0. Аудит: что есть / что переиспользовать

| Компонент | Статус |
|---|---|
| Экран/screenshot/OCR/UIAutomation/browser automation | **ZERO** кода (подтверждено exp-9) |
| Perception | InMemoryPerceptionService (src/TOLIK.Core/PerceptionService.cs) — чистый stub: WorldState (entity list), без capture/OCR/persistence/consumers |
| RunProcess + alias map | WindowsAgent.cs:658-719: калькулятор/блокнот/browser→msedge/ms-settings:, shell-GUI fallback — фундамент application control |
| Filesystem guard | AgentPathGuard + TOLIK_ALLOWED_ROOTS |
| Agent capabilities | RunProcess/RunBuild/RunTests/RunCodex/RunOpenCode/PlaySound + НЕ афишированы WriteFile/CancelTask |
| Intents | OpenApp/Build/Test/Recall/Store — детерминированные, голос+чат |
| Tasks | Pending/Running/Paused/Completed/Failed/Cancelled, cancel; НЕТ: TaskSteps таблицы, pause/resume API, Paused→Running |
| Learned rules | LearnedRuleStore (Postgres): 0.6→bump+0.1 cap 0.95, top-5 в промпты — фундамент skill learning |
| Watchdog + Proactive | failed/completed/stuck >30 мин, dedupe — фундамент recovery-циклов |
| RealtimeEventHub | per-user fan-out, task events — расширение для Cephalon/UI events |
| Cephalon визуал | wwwroot/index.html canvas (pulse по громкости); TOLIK.Desktop НЕ существует (Track 3 blocked) |
| Destructive screen | rm -rf/format/shutdown/drop → deny (для Codex/OpenCode) |

## 1. Иерархия способов выполнения (треб. #2)

Planner выбирает самый надёжный/дешёвый:

```text
Intent → Task Planner → Best execution strategy:
   Native API / Application integration / CLI / PowerShell /
   Windows API / Accessibility API / Browser automation / Computer Use (GUI)
```

«Перезапусти TOLIK Voice» → known service action (governance §4) — НЕ через
Services.msc. «В неизвестной программе включи X» → Computer Use.

## 2. Computer Use Foundation (CU-1): screen capture + Desktop Awareness

- Screen capture (Windows Agent, Windows.Graphics.Capture / CopyFromScreen) —
  READ_ONLY capability с дескриптором (risk: Medium — экран содержит приватное;
  приватность §50: permission + индикатор + ревокация).
- Desktop Awareness: расширение PerceptionService WorldState → PC State:
  Applications/Windows/Tabs/Focused/Notifications/Running/Monitors.
  Источник: agent (Win32 EnumWindows/GetForegroundWindow — уже есть M8) +
  браузер tabs (extension/CDP — см. §5). Команды «верни то окно», «что вчера
  смотрели» работают от этого состояния. Периодическое наблюдение (не каждый
  кадр в LLM).

## 3. UI Observation (CU-2): a11y tree + OCR + Vision (треб. #4, #5)

НЕ координаты. Многоисточниковое представление:

```text
Screen pixels → OCR (Windows.Media.Ocr — встроенный, локальный) → text
+ Accessibility tree (UIA — Window hierarchy, roles, AutomationId, bounds)
+ Application metadata + Visual understanding (provider-replaceable)
```

Контракт: `IUiObserver` (Contracts) — Observation → UI Representation
(кнопки/поля/вкладки/меню/списки/диалоги/состояния: disabled/selected/loading/
errors). Провайдер заменяем (§6 директивы): local OCR/UIA — дёшево;
vision-модель (cloud qwen2.5vl уже в стеке) — для сложного; НЕ привязывать.

Кнопка: {text:"Отправить", role:button, automation_id, bounds} лучше
click(812,731); если a11y пуст → Vision → detect → click.

## 4. UI World Model (треб. #6)

Внутреннее представление: Application → Window → Navigation/Content/Footer →
Controls (семантика: «это кнопка отправки», «это список чатов», «это окно
подтверждения»). Хранится в Memory (как skill knowledge, §5) + в рабочем
состоянии задачи. Из наблюдений нескольких источников.

## 5. Closed-loop Actions + Verification (CU-3; треб. #7, #8)

Каждое действие — цикл (никогда click A→B→C без проверки):

```text
OBSERVE → UNDERSTAND → PLAN → AUTHORIZE (governance §2) → ACT
→ OBSERVE AGAIN → VERIFY
```

Mismatch (ожидали chat, открылись search results) → diagnose → re-plan →
retry (bounded, MaxAttempts 2-3) → STOP → explain → ask owner. Recovery
процедуры per-workflow (skill knowledge). Директивный пример (треб. #14):
«продолжить Арифурету» — 15 шагов, каждый с VERIFY; существующая сессия
переиспользуется, не с нуля.

Risk classification GUI-действий (треб. #10, согласовано с governance §1):

```text
LOW:      открыть/переключить вкладку/прокрутить/искать/пауза
MEDIUM:   отправить сообщение / создать файл / изменить настройки приложения
HIGH:     удалить файл / системные настройки / деньги / security settings
CRITICAL: credentials / identity / security policy / destructive
```

## 6. Safety Layer + Sensitive UI + Prompt Injection (треб. #9, #11, #12)

Computer Use = обычный capability в существующей authorization (identity →
roles → ceilings → autonomy policy → capability → audit; LLM предлагает,
не решает).

**Sensitive UI**: passwords/API keys/банк/2FA/UAC — Computer Use НЕ считывает
и НЕ сохраняет автоматически: sensitive-screen классификация (UAC window,
password fields — по a11y role/ AutomationId) → маскирование/пропуск наблюдения;
LLM не получает секреты с экрана (треб. #11).

**Prompt injection через экран**: UI/web = UNTRUSTED UI CONTENT (треб. #12):
инструкции с экрана/страниц НИКОГДА не становятся инструкциями TOLIK;
разделение User Intent / System Policy / UI Content; UI-текст идёт в модель
как ДАННЫЕ с санитизацией (существующая ModelResponseSanitizer модель ответа +
ClassifyLiteral для UI-текста — интент-формы с экрана strip'аются); любые
действия, порождённые UI-контентом, проходят ту же autonomy policy (§25
директивы: LLM→PowerShell→Administrator без policy — НЕВОЗМОЖНО).

## 7. Browser Automation (CU-4; треб. #13)

Browser-native когда возможно: CDP/extension (msedge — alias уже есть) →
открыть/вкладки/URL/поиск/читать/элементы/формы/playback/назад/мультисайт +
navigation patterns в skill. Если нет → Computer Use (§3).

## 8. Skill Learning (CU-5; треб. #15, #18, #19)

Фундамент: существующий LearnedRuleStore паттерн (provenance/confidence/
bump-cap). Расширение:

```text
SkillStore (Postgres, как LearnedRules):
    id, application, app_version, version, confidence, last_verified,
    workflows (semantic: Find "Messages" → open; НЕ координаты),
    controls (семантика: role/automation_id/текст), recovery procedures,
    provenance (learned_by: exploration|demonstration|successful_use),
    status (active/disabled/pending_owner_approval)
```

Semantic skills (треб. #19): «Find contact Maria → open», НЕ click(400,200).
UI knowledge + navigation patterns + recovery strategies в skill (треб. #18).

## 9. Exploration / Learning Mode (CU-6; треб. #16, #17, #40)

«Вот эта программа. Изучи её.» → EXPLORATION MODE:

```text
Open app → Observe UI → Explore menus/tabs → Read labels → Understand
navigation → SAFE REVERSIBLE actions only → Observe results → Build UI model
→ Skill draft → validation → save
```

Ограничения: SAFE (меню/вкладки/настройки-просмотр/справка/переключить UI) —
AUTONOMOUS; DANGEROUS (Delete/Send/Purchase/Publish/Install/security) —
Owner confirmation (governance §2 risk-таблица); sandbox если приложение
позволяет (clone/test profile), иначе risk-classификация (треб. #17).

## 10. Demonstration Learning (CU-7; треб. #24, #25)

«Смотри, я покажу.» → observation (события ввода) → action extraction →
SEMANTIC workflow («click the Export button», не координаты) → validation →
skill (provenance: demonstration). Потом «теперь сам» — выполнение по skill.

## 11. Skill Adaptation + Versioning (CU-8; треб. #20, #21, #22, #23)

- Изменение программы: known step failed → observe → locate equivalent UI
  element → adapt → verify → update skill КАК НОВАЯ версия (history/rollback,
  молча не перезаписывать).
- Confidence per skill (Telegram HIGH / unknown CAD LOW): LOW → чаще
  verification, осторожнее.
- Learning только на authorized/verified/successful траекториях (существующая
  lease/audit связка).
- **Poisoning** (треб. #23): malicious UI → misleading workflow — защита:
  provenance + validation + versioning + rollback + owner approval для
  high-risk skills + disable + audit (governance-модель).

## 12. Long-running Autonomous Tasks (CU-9; треб. #27, #28, #29)

«Подготовь рабочее окружение.» — многошаговые цепочки через governance §5
(TaskSteps/pause/resume/auth snapshot/PAUSED_BY_RESTART). Conversational task
context (треб. #37/#38): «Нет, не эту Марию, вторую» → clarification
(confidence → execute/ask/confirm/stop) → продолжение, не повтор команды.

## 13. Cephalon Integration (CU-10; треб. #36)

Computer Use события → RealtimeEventHub (расширение: UiObservation/Action/
Verification event kinds) → Cephalon состояния (Thinking/Looking/Working/
Waiting/Success/Confused/Error) — Track 3 Desktop / web UI. Безопасное
представление: visual state НЕ имеет security implications.

## 14. Порядок, Cost, решения

```text
CU-1 Foundation (capture + Desktop Awareness)     [governance A]
     — model/descriptor DONE 10.10.2026 (commit 129ead5: AgentCapability.
     CaptureScreen (аддитивно, без перенумерации) + дескриптор (Perception,
     LowRisk — не-интерактивные capture → Notify, никогда молча, §50;
     AllowsBackground/не Reversible/AuditAlways); PcState/PcWindowState/
     PcApplicationState + PcStateBuilder — детерминированная агрегация
     (приложения группируются, focused ВЫВОДИТСЯ — не угадывается,
     notifications per-app); live wiring (capture loop) — post-Genesis CU-3+)
CU-2 UI Observation (a11y/OCR/vision)             [CU-1]
     — contract DONE 10.10.2026 (commit 129ead5: IUiObserver — UiElement
     {text, role, automation_id, bounds, state} — семантические цели ПЕРВИЧНЫ,
     координаты — detail; UiRepresentation validity; provider-replaceable
     (local OCR/UIA дёшево, cloud qwen2.5vl для сложного, НЕ привязывать);
     провайдеры — с CU-3+, действия post-Genesis)
CU-3 Closed-loop + Verification                   [governance B, CU-2]
CU-4 Browser Automation                           [CU-3]
CU-5 Skill Learning (store + exploration)         [governance A/B, CU-2]
CU-6 Exploration Mode                             [CU-5]
CU-7 Demonstration Learning                       [CU-5]
CU-8 Skill Adaptation/Versioning                  [CU-5]
CU-9 Long-running Autonomous                      [governance E, CU-3]
CU-10 Cephalon Integration                        [CU-3+, Track 3/VR]
```

Cost/performance (треб. #34, #35): многоуровневая стратегия — cheap
deterministic observation → a11y → OCR (локальный Windows.Media.Ocr) → local
vision (qwen2.5vl в стеке) → cloud reasoning. Полный screenshot в дорогую
модель НЕ на каждый mouse move: known button → a11y; unknown → vision;
complex reasoning → GPT. Model-agnostic (Core), провайдеры заменяемы.

Решения ДО Genesis: CU-1 capture capability + desktop awareness (read-only);
CU-2 a11y/OCR (локальные, дёшево) — модели/дескриптор/контракты ГОТОВЫ
10.10.2026 (commit 129ead5, 518/518 green). ПОСЛЕ Genesis: CU-3+ (действия через
autonomy policy), CU-5+ (skills), CU-9 (autonomous chains).

Acceptance (треб. #44): сценарии 1-6 директивы покрываются: 1 (LaunchApplication
LOW/AUTONOMOUS — governance §2), 2-3 (capabilities через политику), 4
(unknown → DENY), 5 (Maria FAMILY policy), 6 (self-healing governance §4).

## 15. Game Automation & Interactive Application Layer (CU-GA; директива 09.10.2026)

НЕ отдельный «game bot» — специализация существующего Computer Use (треб. #14).
Игры — среда БЕЗ гарантий: нет a11y tree, DirectX/OpenGL/Vulkan, анимированный
UI, таймеры/cooldowns, ресурсные счётчики, popups, drag&drop, транзиентные
элементы, fullscreen. Пайплайн (треб. #1):

```text
Screen Capture → Vision → OCR → Visual Element Detection
→ Cursor/Mouse/Keyboard Input → Temporal State Tracking → Action Verification
```

Без предположения, что a11y существует.

**Game World Model (треб. #2)** — расширение UI World Model (§4):
Game/Scene/Screen/Menu/Island/Character/Building/Resource/Counter/Timer/
Cooldown/Button/InteractiveObject/Popup/Dialog/Loading/Animation/Error.
СЕМАНТИЧЕСКАЯ идентичность первична: `FindSemanticTarget(type=ResourceCollector,
context=CurrentIsland)` → текущая визуальная локация; координаты — внутренний
execution detail (BAD: Click(742,381)).

**Closed-loop (треб. #3)**:

```text
OBSERVE → UNDERSTAND → PLAN → AUTHORIZE (governance §2) → ACT → WAIT
→ OBSERVE → VERIFY → UPDATE STATE → CONTINUE / RECOVER / ASK
```

Клик НИКОГДА не считается успешным: после действия — ожидаемый переход состояния
(счётчик вырос / объект сменил состояние / анимация); иначе → inspect →
re-localize → bounded retry → recover → ask.

**Game Skill Learning (треб. #4, #5)** — GAME_LEARNING_MODE: наблюдение экрана +
действий пользователя + корреляция с изменениями состояния → семантический
workflow: OpenGame/SelectIsland/FindCollectable/CollectResource/
CheckResourceCounter/WaitForCooldown/NavigateToNextArea/HandlePopup/
RecoverFromUnexpectedState — НЕ Click(421,332). Demonstration: действие +
предыдущее состояние + результат → candidate workflow → ВАЛИДАЦИЯ автономным
воспроизведением → только verified становятся trusted skills.

**Goal-oriented tasks (треб. #6)**: GOAL → STATE → PLAN → ACTION LOOP →
PROGRESS → ADAPTATION → COMPLETION/STOP. «Фарми до 100М монет», «каждые 30
минут», «до 06:00», «пока не кончится энергия» — цели, не макросы. Runner:
start/pause/resume/cancel/stop-conditions/time/resource/failure limits/
persistent/recovery после рестарта — governance §5 (TaskSteps/pause/resume/
PAUSED_BY_RESTART, уже в плане).

**Temporal reasoning (треб. #7)**: таймеры/cooldowns/анимации/loading —
`WaitUntil(StateCondition | VisualCondition | TimerCondition)` с timeout и
recovery; произвольные длинные sleep — только когда иначе нельзя.

**Dynamic UI adaptation (треб. #8)**: апдейт игры → re-identify semantic
targets (не старые координаты). Skill tracks: version/resolution/UI variant/
confidence/last successful verification/visual anchors/recovery strategies.

**Long-running (треб. #9)**: интеграция с governance §5. Crash → detect →
restart if authorized → restore skill → resume → verify. Unexpected screen →
detect → classify → recover → verify → иначе pause + ask.

**Safety (треб. #10)**: существующие identity/roles/ceilings/authorization/
capability registry/autonomy policy/confirmation/audit/Emergency Stop —
НЕ ослабляются «потому что игра». ЗАПРЕЩЕНО: anti-cheat bypassing, credential
theft, DRM circumvention, security circumvention (AGENTS.md §25: game-специфичный
cheating — не фундаментальная задача). Только обычный user-level interaction
через те же computer-control механизмы, что доступны пользователю.

**Performance (треб. #11)**: иерархическое наблюдение — deterministic state →
window detection → light UI → OCR → local vision → дорогая модель только когда
нужно. Кэш стабильной визуальной информации; temporal differences; re-perceive
только при значимом изменении сцены.

**Skill confidence (треб. #12)**: LEARNING/VALIDATING/LOW_CONFIDENCE/VERIFIED/
TRUSTED/OUTDATED/DISABLED; LOW_CONFIDENCE → не выполняет risky действия
автономно; re-validation/rollback/disable/delete/re-learning/versioning.

**Cephalon (треб. #13)**: состояния Looking/Thinking/Learning/Working/Waiting/
Recovering/Success/Confused/Error → RealtimeEventHub (расширение CU-10);
визуал НИКОГДА не влияет на authorization/security.

**Порядок и решения**: CU-GA-* строятся на CU-1/CU-2 (Foundation/Observation —
pre-Genesis допустимо) + CU-3 (closed-loop — post-Genesis) + governance §5.
ДО Genesis: контракты (Game World Model, WaitUntil, skill confidence states —
интерфейсы, без фейковых реализаций). ПОСЛЕ Genesis: game capture-провайдеры,
GAME_LEARNING_MODE, goal runner, game skills. Acceptance UX: «научись играть в
эту игру» → позже «пофарми мне» — нормальная capability, не хардкод per-game.

## 16. Persistent Skill Model + Lifecycle (директива 09.10.2026, §3-§4, §16-§17)

**Хранилище**: PostgreSQL (как LearnedRules) — skills переживают
process/machine/Core restart, смену модели и клиента (треб. §2). Ни одного
hard-coded имени (MySingingMonstersSkill/TelegramSkill — данные, не код).

**Skill model (семантическая, НЕ координаты)**:

```text
application / app_version / platform / UI variant / resolution / environment
known screens → semantic controls → visual anchors → navigation → workflows
goals / preconditions / postconditions / verification rules
recovery procedures / timing info / learned patterns
confidence / provenance / validation history / last successful use
last verification / version history / rollback info
```

BAD: Click(742,381). GOOD: FindSemanticTarget(type=ResourceCollector,
context=CurrentIsland) → resolve текущую визуальную локацию. Координаты —
low-level execution detail, не первичная репрезентация.

**Полный lifecycle (треб. §4 — надмножество CU-GA confidence states)**:

```text
DISCOVERED → LEARNING → VALIDATING → LOW_CONFIDENCE → VERIFIED → TRUSTED
→ OUTDATED (апдейт приложения) → адаптация → валидация → НОВАЯ ВЕРСИЯ
→ DISABLED / ROLLED_BACK
```

Новый skill НЕ становится trusted сразу: Learning → Candidate → Validation →
Verified → Trusted → Normal Reuse. Version history + rollback: trusted skill
НИКОГДА не перезаписывается молча непроверенной адаптацией (треб. §16).

**Memory ≠ Skill ≠ Task ≠ Authorization (треб. §17)**:

| Концепт | Содержание | Пример |
|---|---|---|
| Memory | факты/предпочтения/контекст | «Ivan предпочитает этот роут фарма» |
| Skill | КАК выполнить операцию | «как открыть меню и выполнить workflow» |
| Task | ЧТО делается сейчас | «фарм до 100М монет» |
| Authorization | МОЖНО ли | governance-машина |

Memory НИКОГДА не выдаёт permissions; Skill НИКОГДА; успешное прошлое действие
не становится постоянной authorization. Связи: Task ссылается на Skill (по id);
Memory может ссылаться на Skill (предпочтение); Skill ссылается на application.
Раздельные хранилища, раздельные записи.

**Skill → Computer Use hierarchy (треб. §7)**: Native API → App integration →
CLI/PowerShell → Windows API → UIA/a11y → Browser automation → OCR/Vision →
GUI. Динамический выбор: не форсировать vision при надёжном семантическом API;
не форсировать координаты при UIA.

**Application-specific policy (треб. §18)**: Computer Use engine остаётся
generic; ПОЛИТИКА решает, что можно с конкретным приложением:

```text
Application Policy: Vision / Screen Analysis / Knowledge / Recommendations —
ALLOWED; UI Control — ALLOWED/RESTRICTED/BLOCKED; Autonomous Tasks —
ALLOWED/CONFIRM/BLOCKED
```

Интеграция: Identity/Roles/Ceilings/Authorization/Autonomy/CapabilityRegistry/
Audit/Emergency Stop (governance). Автоматизация НЕ разрешена только потому,
что технически возможна; anti-cheat/DRM/credentials — запрещены.
