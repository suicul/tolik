# TOLIK — Personal Digital Operating Layer

> TOLIK — не чат-бот и не оболочка над LLM. Это одна непрерывная персональная
> система: идентичность, память, голос, инструменты и ограниченная автономность
> поверх сменяемых моделей.

Полное назначение системы — `docs/reference/Описание проекта.docx`. Целевая
архитектура и инварианты — `docs/reference/TOLIK__Harness.md`. Архитектурные
правила для разработки — `AGENTS.md`. Иерархия клиентов — `docs/clients.md`.

## Архитектура

```text
Windows UI (главный клиент) ─┐
Android (продолжение)      ─┼─► TOLIK Core (ASP.NET Core)
Linux CLI (тонкий клиент)  ─┘        │
                                     ├── Identity + Lifecycle
                                     ├── Memory + Provenance
                                     ├── Authorization + Audit
                                     ├── Model Router ──► облачные LLM
                                     └── Voice Runtime (STT / speaker / TTS)
                                              │
                                     Agent Protocol
                                              │
                                     Windows Agent
                                              │
                                     OS / файлы / процессы / инструменты
```

Принцип: **модель предлагает → Core проверяет → Security разрешает → Agent
исполняет → Core проверяет результат.** LLM не является корнем доверия.

## Компоненты

| Компонент | Назначение |
| --- | --- |
| `src/TOLIK.Core.Host` | Composition root, HTTP API, hosted services |
| `src/TOLIK.Core` | Доменная логика: планировщик, память, голос, ум |
| `src/TOLIK.Infrastructure` | PostgreSQL, модели, авторизация, криптография |
| `src/TOLIK.Agent` | Исполнитель на Windows: файлы, процессы, UIA, захват голоса |
| `src/TOLIK.Desktop` | Windows-клиент: цефалон, состояние, озвучка |
| `clients/TOLIK.Android` | Android-клиент (Kotlin): чат, голос, цефалон |
| `services/voice` | Python-сервис: faster-whisper, edge-tts, XTTS, проверка говорящего |

## Модели

**Облако — рабочий путь, локальный LLM — только аварийный.**

| Маршрут | Модель | Назначение |
| --- | --- | --- |
| Simple | `gpt-6-luna` | обычный чат и голос |
| Coding | `gpt-6.1-sol` | работа с кодом |
| Reasoning | `gpt-6.1-terra` | сложные рассуждения |
| Vision | `gemini-3.8-flash` | изображения (потребителя в коде пока нет) |
| Offline | `qwen2.5:3b-instruct` | **только** при недоступности облака |

Провайдер — `tooken` (`https://tooken.club/v1`). Локальная модель не
прогревается: RTX 3060 остаётся доступной для игр.

## Голос

| Этап | Реализация | Ресурсы |
| --- | --- | --- |
| STT | faster-whisper `medium`, CPU int8 | 0 VRAM, ~2.6 ГБ RAM, ~2.6 с на реплику |
| Проверка говорящего | CampPlus-эмбеддинги, локально | CPU |
| TTS (по умолчанию) | edge-tts (облако) | 0 VRAM |
| TTS (опционально) | XTTS, GPU | ~2 ГБ VRAM, включается флагом `-WithXtts` |

`base` не подходит для русского: на живой речи выдаёт смесь английского и
мусора. Замеры и обоснование — в сообщении коммита `e314e14`.

## Запуск

```powershell
.\TOLIK.bat                     # стек + агент + UI
.\scripts\start-tolik.ps1 -NoAgent       # только стек
.\scripts\start-tolik.ps1 -WithXtts      # + нейроголос XTTS (занимает VRAM)
.\scripts\stop-tolik.ps1                 # остановить (данные целы)
```

Требуется Docker и запущенный демон. Ollama не обязательна — она нужна только
для аварийного локального маршрута.

## Проверка

```powershell
dotnet build TOLIK.sln
dotnet test  TOLIK.sln      # требуется живая PostgreSQL (docker compose up -d postgres)
```

Актуальный baseline: **822** Foundation + **230** Desktop тестов, 0 падений.

## Документация

- `docs/clients.md` — иерархия клиентов и что считается готовностью Windows-клиента.
- `docs/ARCHITECTURE.md` — устройство Core.
- `docs/SECURITY.md`, `docs/authorization.md`, `docs/privacy.md` — безопасность.
- `docs/MEMORY.md` — память и провенанс.
- `docs/voice-runtime-v2.md`, `docs/voice-identity.md` — голосовой контур.
- `docs/ROADMAP.md` — живой план развития.
- `docs/LIVE-DEMO.md`, `docs/REMOTE-ACCESS.md` — запуск и удалённый доступ.

## Статус

Фаза — PRE-GENESIS hardening. Функциональная готовность по аудиту 11.2026:
≈ 50 % до эталона; главные разрывы — приватность памяти при нескольких
говорящих, поиск по памяти, отсутствие ввода в Windows-клиенте.
