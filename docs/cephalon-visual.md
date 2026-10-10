# TOLIK Cephalon — Visual Representation (предложение, 10.10.2026)

Ответ на директиву владельца: «нет визуального представления цефалона — подумай».
Находка: визуальное представление СУЩЕСТВУЕТ — `interface/cephalon.html` (eye-анимация,
статус-бар, субтитры, voice/chat) — но его скрипт был мёртв: дублирующиеся `const`
объявления (SyntaxError не давал выполниться всему модулю) + `response.json()` на
несуществующей переменной + дубликаты `api/apiPost`. Исправлено 10.10.2026 — страница
теперь запускается и показывает цефалона.

## 1. Принцип (уже принятый, подтверждается)

> **TOLIK has state; animation is interpretation.** Никаких предзаписанных аним-клипов —
> визуал читает НАСТОЯЩЕЕ состояние и рендерит его. Состояние не выдумывается: если
> TOLIK не думает — глаз не думает. Один дизайн-источник: `interface/cephalon.html`
> → Desktop (браузер UI) → Android (аватар) → VR (3D-продолжение).

## 2. Expression State — детерминированный микс (не LLM-вывод)

Visual state = детерминированная смесь из РЕАЛЬНЫХ источников (никогда из текста
модели). Приоритет сверху вниз (security-состояние всегда перекрывает):

| # | Источник | События/данные | Visual |
|---|---|---|---|
| 1 | **Security** (Core) | `locked: true` из /health; tamper/критические события | `locked` (красный, веки почти закрыты) — fail-closed визуально |
| 2 | **Authorization** (Core) | отказ в разрешении, ConfirmationRequired отклонён | `denied` |
| 3 | **Voice runtime** (M0-M8) | TTS играет → speaking; mic слушает → listening; barge-in → мгновенный переход | `speaking` (зелёный iris) |
| 4 | **Task lifecycle** (RealtimeEventHub) | TaskStarted/ToolStarted → thinking; TaskCompleted/ToolCompleted → idle; TaskFailed → denied-короткий импульс | `thinking` (синий, активные веки) |
| 5 | **Mind** (MI-0..2, когда экспортировано) | ActivityLevel REST → `resting` (медленное дыхание, приглушённый iris); curiosity high → лёгкое усиление движения внимания; attention target → лёгкий поворот зрачка (routing only, НЕ авторизация) | `resting`/`curious` |
| 6 | **Default** | нет активности | `idle` (дыхание) |

Правила:
- Эмоция/настроение НЕ рисуются, если их нет в состоянии (mind §5: никогда не фейк).
- `speaking`/`thinking` из реальных событий, никогда из того, что TOLIK «должен выглядеть занятым».
- Subtitle: только реальный текст (ProactiveMessage + TTS-текст), скрывается по таймеру.
- Статус-бар: реальные /health компоненты (core/voice/agent/memory) — уже есть в референсе.

## 3. Поверхности

```text
cephalon.html (design source, 2D canvas, исправлен 10.10.2026)
   │
   ├── Desktop (сейчас): отдельная страница работает; следующий шаг —
   │     встроить глаз как панель в существующий workspace UI (wwwroot/index.html)
   │     и подключить РЕАЛЬНЫЕ данные: WS /events/ws + /health (есть) +
   │     voice runtime state (есть)
   ├── Android (AN-1+): тот же Expression State, рендер в приложении
   │     (thin: RealtimeEventHub → состояние → аватар; без Core logic)
   └── VR (после Desktop Phase 1, Unity + Meta XR SDK): та же дизайн-система
         как 3D-продолжение (объёмный цефалон: idle/speaking/working/alert
         от RealtimeEventHub; пространственный звук)
```

## 4. Pre-Genesis граница

- **Pre-Genesis допустимо**: исправление референса (сделано), интеграция глаза в
  существующий browser UI (thin, read-only поверх существующих WS/health — новых
  эндпоинтов не требует), Expression State контракт как документ.
- **Post-Genesis**: экспорт MindState в host (визуал читает boredom/curiosity/
  activity — сейчас MindState не wired в host по pre-Genesis правилу), VR 3D,
  voice-привязка с живой церемонией.

## 5. Что НЕ делать

- Никаких аним-клипов/предзаписанных видео (принцип: state → interpretation).
- Никаких фейковых состояний («выглядит умным», «выглядит занятым») — только real state.
- LLM не управляет визуалом напрямую — только через состояние (детерминированный микс).
- Содержимое экрана/приватные данные никогда не попадают в визуал.
- Attention target — routing only, никогда авторизация.
