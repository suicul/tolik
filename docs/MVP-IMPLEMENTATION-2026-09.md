# TOLIK MVP — карта компонентов (сессии сырого кода, сентябрь 2026)

> Документ описывает всё, что написано в сессиях «чистого кода» (24.09.2026),
> где находится каждый компонент и **куда смотреть при ошибках**.
> Статус: код написан БЕЗ сборки — при первом `dotnet build` возможны мелкие
> ошибки компиляции (см. раздел «Непроверенное»).

---

## 1. Полный реестр компонентов

### Сессия 1 (базовый MVP)

| # | Компонент | Файлы |
|---|-----------|-------|
| 1 | Голосовой Python-сервис | `services/voice/server.py` (Whisper STT + CosyVoice TTS) |
| 2 | STT-клиент Core | `src/TOLIK.Contracts/SttContracts.cs`, `src/TOLIK.Core/WhisperSttProvider.cs` |
| 3 | Голосовые эндпоинты | `Program.cs`: `/voice/transcribe`, `/voice/turn` |
| 4 | Компакция памяти | `src/TOLIK.Core/MemoryCompactionService.cs` + `MemoryCompactionBackgroundService.cs` |
| 5 | Колонка CompactedAt | миграция `20260924090000_MemoryCompaction.cs` |
| 6 | Durable-напоминания | `ProactiveEntities.cs`, `ProactiveStore.cs`, миграция `20260924091000`, `/proactive/*` |
| 7 | Feedback → правила | `LearnedRuleStore.cs`, `/tasks/{id}/feedback`, `/rules` |
| 8 | run_process через Агента | `Program.cs` (/chat, /tasks/execute), `WindowsAgent.cs` |
| 9 | Агент: авто-reconnect | `WindowsAgent.RunCommandLoopAsync` (backoff 5с→80с) |
| 10 | Фикс JSON ImmutableHashSet | `src/TOLIK.Contracts/ImmutableSetJsonConverter.cs` |
| 11 | Realtime-события | `RealtimeEventHub.cs`, `/events/ws`, хук `TaskLifecycle.EventPublished` |
| 12 | Web-консоль | `wwwroot/index.html` |
| 13 | CLI полный | `clients/TOLIK.Cli/*` (bootstrap/transcribe/say/listen/remind/sound/memory/agents) |
| 14 | Linux-клиент | `clients/tolik-linux.sh` |
| 15 | Скрипты | `scripts/start-tolik.ps1`, `transcribe-dataset.ps1`, `test-e2e.ps1` |

### Сессия 2 (интеллект MVP) — эта

| # | Компонент | Файлы | Что делает |
|---|-----------|-------|-----------|
| 16 | **IntentRouter** | `src/TOLIK.Core/IntentRouter.cs` | детерминированная классификация фраз: открой/напомни/запомни/что помнишь/собери/тесты/смешно/семейный мем/режим — БЕЗ LLM (§24) |
| 17 | **Голосовые режимы** | `src/TOLIK.Core/VoiceRuntimeState.cs`, `/status/voice` | глобальный режим Normal/Night/Gaming, переключается чатом и WebUI |
| 18 | **Чат: intent-обработчики** | `Program.cs` → switch(intent.Kind) | запомни→память; семейный мем→humor+lore+память; смешно→humor; помнишь→поиск памяти; собери/тесты→RunBuild/RunTests через Агента |
| 19 | **Voice-команды без модели** | `ProcessSimpleIntentAsync` | «открой калькулятор» голосом работает даже когда модель недоступна |
| 20 | **Soundboard** | `AgentCapability.PlaySound`, `WindowsAgent.PlaySound`, `/audio/play`, `/audio/library`, `SoundboardRateLimiter` | звуки на ПК, rate limit 1/10с (§17), каталог `audio/{reactions,memes,family,system}` |
| 21 | **Sessions: lifetime + sliding** | `GetSessionLifetime` (config `TOLIK_SESSION_LIFETIME_HOURS`, по умолч. 168ч=7д), `IdentityService.AuthenticateAsync` sliding-extention | сессии больше не истекают за 15 минут |
| 22 | **Codex/OpenCode промпты** | `WindowsAgent.RunAssistantCliAsync` | формат `dir|prompt` + экран деструктивных директив (rm -rf, drop table…) — §22 |
| 23 | **ModelRouter fallback** | `ResolveWithFallback`, `ChatModelSelector` | cloud→offline→simple; код-вопросы → Coding-маршрут |
| 24 | **Ежедневный обзор** | `src/TOLIK.Core/DailyReviewService.cs`, `DailyReviewBackgroundService.cs` | «Итог дня» → эпизодическая память, идемпотентно по дате (§19) |
| 25 | **WebUI: голос** | `wwwroot/index.html` (MediaRecorder + `/voice/turn`) | держишь 🎤 — TOLIK отвечает голосом |
| 26 | **Docker VPS** | `src/TOLIK.Core.Host/Dockerfile`, `.dockerignore`, `docker-compose.yml` profile `vps` | §55: `docker compose --profile vps up -d core` |
| 27 | **CLI sound** | `tolik sound --name FILE [--category X]` | 429 = rate limit |

---

## 2. Куда смотреть при ошибках (диагностическая таблица)

### Интенты и чат

| Симптом | Причина | Где смотреть |
|---------|---------|--------------|
| «запомни X» уходит в модель вместо памяти | IntentRouter не сматчил — регекс | `IntentRouter.StoreMemoryRegex` — требует `запомни` в начале строки; тест: `IntentRouter.Classify("запомни: тест")` должен дать StoreMemory |
| «что я говорил про X» → ответ модели | RecallRegex не сматчил | `IntentRouter.RecallRegex`; формулировки «что про X», «помнишь X» |
| «собери проект X» → 503 агент | Агент не подключён / нет RunBuild | `GET /agents` — capabilities; `WindowsAgent.CreateRegistration` |
| Хочется новый паттерн | — | добавить в `IntentRouter.Classify` + case в `Program.cs` switch; паттерны ДОЛЖНЫ быть детерминированными |
| «открой» открывает не то | алиас | `ResolveAppAlias` в `Program.cs` (калькулятор→calc.exe, …) |

### Голос

| Симптом | Причина | Где смотреть |
|---------|---------|--------------|
| `/voice/turn` 503 | Python-сервис не запущен | `python services/voice/server.py`; `GET :9660/health`; `TOLIK_STT:Endpoint` в appsettings |
| TOLIK молчит (`audio: null`) | глобальный режим Night ИЛИ CosyVoice недоступен | `/status/voice` → `runtimeMode`, `ttsAvailable`; VoiceRuntimeGate: в Night голос выключен намеренно |
| Переключение режима не работает | `TrySwitch` вернул false | `VoiceRuntimeState.TrySwitch` — допустимы: normal/обычный, gaming/игровой, night/ночной |
| WebUI 🎤 не пишет | нет mic-разрешения в браузере | MediaRecorder требует https ИЛИ localhost; проверь `navigator.mediaDevices` |

### Сессии

| Симптом | Причина | Где смотреть |
|---------|---------|--------------|
| 401 через неделю | сессия истекла (sliding не спасает при простое) | `IdentityService.AuthenticateAsync` (sliding: продлевается при активности, если осталось < 1ч); `TOLIK_SESSION_LIFETIME_HOURS` |
| Токен украден | — | `POST /auth/session/{id}/revoke`; `POST /auth/devices/{id}/revoke` (§10) |

### Soundboard

| Симптом | Причина | Где смотреть |
|---------|---------|--------------|
| 429 | rate limit 1/10с | `SoundboardRateLimiter` — окно в константе `Window` |
| 400 sound_not_available | файла нет / имя с путями / категория не из списка | `WindowsAgent.PlaySound`: root из `TOLIK_AUDIO_ROOT`, категории reactions/memes/family/system |
| Звук не слышно | Агент-процесс в сессии без аудио | NAudio `WaveOutEvent` играет в сессии Агента — запускай Агента в интерактивной сессии |

### Docker/VPS

| Симптом | Причина | Где смотреть |
|---------|---------|--------------|
| core-контейнер не стартует | миграции/конфиг | логи `docker compose logs core`; `ConnectionStrings__Postgres` хост=postgres (не localhost!) |
| Build context огромный | не применился .dockerignore | `.dockerignore` в корне; voice_reference/datasets исключены |

### Миграции

| Симптом | Причина | Где смотреть |
|---------|---------|--------------|
| `column "CompactedAt" already exists` | EnsureCreated до Migrate | тесты используют EnsureCreated, Core — только Migrate; не смешивать на одной БД |
| Следующий `dotnet ef migrations add` генерирует DROP | снапшот отстал | `TolikDbContextModelSnapshot.cs`: TolikProactiveEntity + CompactedAt должны быть (добавлены руками) |

---

## 3. Порядок запуска MVP (актуальный)

```powershell
docker compose up -d postgres                          # база
python services\voice\server.py --whisper-model small   # голос (окно 1)
.\scripts\start-tolik.ps1                               # Core (окно 2)
dotnet run --project clients\TOLIK.Cli -- bootstrap --code TLK-BSTEP-2026-INIT   # один раз
dotnet run --project src\TOLIK.Agent -- --core-url http://127.0.0.1:5080 --token (Get-Content ~\.tolik\session-token)  # агент
# WebUI: http://127.0.0.1:5080/  (токен из ~\.tolik\session-token)
```

### Проверочные фразы (быстрый smoke)
```
tolik chat "открой калькулятор"        # агент открывает calc.exe
tolik chat "запомни: я люблю чай"      # → store_memory
tolik chat "что я говорил про чай?"    # → recall_memory
tolik chat "напомни через 2 минуты перерыв"  # → reminder, придёт в /proactive/poll
tolik chat "это наш семейный мем: кот-шпион"  # → family meme
tolik chat "ночной режим"              # → голос выключен в ответах
tolik listen                            # голосовой цикл
```

---

## 4. Непроверенное (первый `dotnet build`)

1. `Program.cs`: `ProcessSimpleIntentAsync` — local function после `ResolveAppAlias`/`GetSessionLifetime`; порядок `using` (`TOLIK.Infrastructure.Models` для `ChatModelSelector` — уже через полный путь).
2. `IntentRouter`: `[GeneratedRegex]` — требует partial class (есть) и C# 11+ (есть).
3. `SoundboardRateLimiter` instance → переменная `soundboardLimiter` в top-level.
4. `DailyReviewService`/`MemoryCompactionService` — одинаковый конструктор-паттерн (primary constructor + ILogger) — проверить namespace `ILogger<>` (`Microsoft.Extensions.Logging` в TOLIK.Core.csproj).
5. `IdentityService` sliding-блок — `db.SaveChangesAsync` внутри AuthenticateAsync: каждый запрос при <1ч остатка пишет в БД — нагрузка ок для personal use.
6. Dockerfile: `.dockerignore` исключает `docs/`, но NOT `src/TOLIK.Core.Host/wwwroot` — убедиться, что publish копирует wwwroot (UseDefaultFiles требует его на рантайме).
7. `tolik-linux.sh` — не запускался; проверить bash-эскейпы в `MSG_JSON`.

## 5. Следующие шаги

1. `dotnet build` → фикс мелких ошибок → `dotnet test` — DONE (224/224 на 02.10).
2. Прогнать `scripts/test-e2e.ps1` (дописать туда sound/remind/intent-проверки)
3. Whisper **medium** (small слабоват для русского) + VAD (silero) перед STT
4. CosyVoice: транскрибировать `voice_reference/` (`scripts/transcribe-dataset.ps1`) → дообучение
5. Модельные ответы в /chat проходят sanitization (§22) — DONE 02.10.2026:
   `ModelResponseSanitizer` (src/TOLIK.Core) — instruction-shaped строки
   (Tolik-addressed + literal intent patterns через IntentRouter.ClassifyLiteral,
   без voice-удобства Normalize) вырезаются из ответа модели до отображения,
   озвучки и записи в память; удалённые строки возвращаются в ответе
   (filteredInstructions) для прозрачности; wired в /chat + /voice/turn.
