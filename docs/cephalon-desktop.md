# TOLIK Cephalon Desktop Embodiment — мастер-план (директива владельца, 10.10.2026)

Директива: визуальное воплощение TOLIK на рабочем столе Windows — оригинальный
цифровой Цефалон (не приложение, не видео, не GIF-библиотека, не виджет).
Художественный ориентир — Ordis (нечеловеческая геометрия, центральный глаз,
свет/энергия), оригинальный дизайн, НЕ копия.

## 1. Главный принцип (абсолютный)

> **TOLIK не проигрывает готовые анимации настроения. TOLIK имеет внутреннее
> состояние, а его тело процедурно выражает это состояние.**

Цепочка: `TOLIK State → Expression State → Expression Composer → Procedural
Animation → Renderer`. Никаких Happy.anim/Sad.anim/Angry.anim — названия могут
существовать как семантические состояния, не как клипы. Короткие процедурные
примитивы — строительные блоки, не библиотека эмоций.

## 2. Границы подсистем (не смешивать)

```text
TOLIK Core (.NET 10)            — состояние, события, авторизация (СУЩЕСТВУЮЩИЙ)
   │  RealtimeEventHub WS (/events/ws) + /health + voice telemetry (M7)
   ▼
Expression State (typed, Contracts)  — снимок состояния выражения [0..1]
   │  обновляется отдельным runtime-компонентом (State Watcher); renderer
   ▼                              НЕ придумывает эмоции сам
Expression Composer (Core, детерминированный) — микс влияний, приоритеты,
   │  переходы; ЧИСТАЯ функция: (state, previous, dt) → output; без LLM
   ▼
Procedural Renderer (WPF, CPU 2D) — рисование из параметров + ограниченная
   │  стохастичность idle (seeded random с границами) + оверлей-механика
   ▼
Desktop Embodiment Host — прозрачное окно, трей, режимы присутствия
```

- Renderer содержит **NO Identity, NO память, NO permissions, NO authorization** (§13
  директивы; SECURITY-модель: renderer — не сущность). Presence Migration ≠ Core
  Migration (IP-4/IP-2).
- **Приоритет тревоги устанавливает Core** (SignalLevel в контракте) — эмоциональная
  модель composer'а НЕ определяет реальную серьёзность ошибки.
- LLM никогда не исполняет код в renderer; Expression DSL (Phase 5) — bounded,
  валидируемый, с версиями/provenance.

## 3. Технический выбор (обоснование)

- **WPF + CPU процедурный 2D-рендер** (DrawingVisual/OnRender + CompositionTarget.
  Rendering). НЕТ новых NuGet-зависимостей (§44: WPF встроенного достаточно);
  НЕТ GPU-рендера — **RTX 3060 остаётся свободной для игр** (AGENTS §6; директива
  §16: VRAM не считать свободной). CPU 2D при capped FPS лёгок. Геометрия —
  параметрическая (слои/глаз/оболочка из малого набора параметров) → перенос
  в VR/3D (VR.md) и шейдер при необходимости.
- Оверлей: прозрачное окно без рамки, always-on-top, PerMonitorV2 DPI, клик-through
  (WS_EX_TRANSPARENT) в пассивном режиме, интерактивный по запросу, трей-иконка
  (show/hide/mode/exit), горячая клавиша (Ctrl+Alt+T). Core НЕ запускается elevated
  ради оверлея; весь Core НЕ переписывается ради графики.

## 4. Expression State — модель параметров (решение)

Директива §4: «используй только те, которые действительно нужны архитектуре».
Модель = RЕАЛЬНЫЕ источники (никогда текст модели):

| Группа | Параметры | Источник |
|---|---|---|
| Когнитивные | attention, thinking, concentration | задачи/planner (есть) |
| Эмоциональные (нужные архитектуре) | amusement, curiosity, frustration, confidence, uncertainty | Mind MI-0..2 (boredom/curiosity/novelty/frustration/satisfaction → отображаются; mood-слой post-Genesis) |
| Социальные | engagement | Presence IP-4 (UserReturned/UserLeft) |
| Голосовые | isSpeaking, amplitude, low/mid/high band energy | TTS stream M0-M8 (реальный аудиопоток, §9) |
| Системные | signalLevel (None/Warning/Error/Critical), processing, taskProgress, recovery, locked | /health + task lifecycle + RealtimeEventHub (есть) |

Все значения [0..1] (+bool), правила нормализации/сглаживания в контракте.

## 5. Composer — семантика микса (решение)

- **Приоритет**: locked (fail-closed визуально) > Critical/Error > Warning > voice
  (speaking — спектр) > task (processing/concentration) > emotion/cognitive > idle.
- **Аудиореактивность** (§9): lowBand → внешняя оболочка (pulse/shellDeformation),
  midBand → центральная геометрия (eyeScale modulation), highBand → частицы
  (particleDensity), amplitude → glow/movement. Нормализация + сглаживание полос;
  без дрожания.
- **Переходы** (§5/§10): экспоненциальное сглаживание per-параметр (previous → target
  по dt, скорость из transitionEnergy + per-параметр rate) — спокойствие →
  раздражение НИКОГДА не мгновенно. Clamped, без NaN.
- **Временная динамика** (§10): кратковременные реакции/настроение — в СОСТОЯНИИ
  (state updater + Mind переходы); composer — чистая функция. Decay/слои
  (reactions/mood/preferences) — Phase 2 integration + post-Genesis.
- **Output** (пример из директивы §5, адаптирован): eyeScale, eyeOpenness, eyeRotation,
  asymmetry, shellExpansion, shellDeformation, rotation, pulseAmplitude, glowIntensity,
  particleDensity, spectrumIntensity, movementSpeed, attentionAngle, attentionIntensity,
  transitionEnergy.

## 6. Фазы (директива §19; реализация поэтапно)

```text
CE-1 Desktop Presence Prototype — overlay host + оригинальная геометрия +
     центральный глаз + procedural idle + трей/режимы/клик-through  [IN PROGRESS
     10.10.2026; designer-лейн; WPF+CPU]
CE-2 State-Driven Renderer — typed Expression State + Composer (детерминированный,
     тесты) + State Watcher (WS /events/ws + /health + voice telemetry) + wiring
     режимов (Focus/Gaming детект — Phase 4 директивы)
CE-3 Voice-Reactive — реальный TTS stream → FFT/frequency bands → параметры;
     паузы/прерывания/barge-in
CE-4 Desktop Awareness — безопасные события рабочего стола (CU-1/CU-2 контракты
     готовы); фокус/уведомления/активность; режимы
CE-5 Expression DSL — декларативные выражения, валидация, sandbox, версии
CE-6 Learned Expressions — история, привычки, feedback (Reward ≠ Permission;
     никогда reward-hacking)
CE-7 Multi-Device — Presence Manager (IP-4), единый контракт, передача присутствия
```

## 7. Pre-Genesis граница

- **Pre-Genesis допустимо**: CE-1 (client-side thin renderer, read-only: Phase 1 без
  Core-вызовов — local idle, честный debug «no Core connection»), CE-2 ядро
  (typed state + composer — детерминированные, тесты), CE-4 контракты (CU-1/2 готовы).
- **Post-Genesis**: mind-driven expression (MindState экспорт в host — MindState не
  wired в host по pre-Genesis правилу), CE-3 полное voice-wiring на живых turn'ах,
  CE-5/6 (DSL/обучение), CE-7 (Presence Migration).
- Церемония отложена владельцем 10.10.2026; Genesis не выполняется, статус не меняется.

## 8. Тесты (директива §18)

Composer: смешивание (amusement+frustration+speaking одновременно → ОДНО смешанное
выражение), нормализация/клэмпинг (без NaN/Infinity), плавные переходы (спокойствие →
раздражение не мгновенно), приоритеты тревог (locked > critical > voice), аудио-
маппинг (low→оболочка/mid→глаз/high→частицы), interruption (speaking → stop плавно),
disconnect (нулевое состояние → честный idle, без фейковой тревоги). Renderer:
режимы/стохастические границы/FPS governor (designer-лейн). Инвариант: эмоции/
выражения/feedback НИКОГДА не меняют permissions/Identity/trust/authorization.
