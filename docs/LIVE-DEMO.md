# TOLIK — первое живое общение (LIVE DEMO)

Проверено 28.09.2026 на Windows 11 + Docker Desktop. Время ответов ниже — замеры
на этом стенде (Ollama на хосте, модель прогрета).

## Быстрый запуск

Двойной клик по `TOLIK.bat` в корне репозитория — поднимет стек, проверит модель,
дождется Core, при необходимости сделает bootstrap, запустит агент в отдельном
окне и откроет страницу цефалона. Консольный вариант с флагами:

```powershell
.\scripts\start-tolik.ps1                # всё сразу (+XTTS, если есть NVIDIA runtime)
.\scripts\start-tolik.ps1 -NoAgent       # только стек (postgres + voice + core)
.\scripts\start-tolik.ps1 -NoBrowser     # без открытия страницы
.\scripts\start-tolik.ps1 -NoXtts        # без нейроголоса (edge/offline цепочка)
.\scripts\stop-tolik.ps1                 # остановить стек (данные целы)
```

Ниже — те же шаги вручную, если нужен контроль над каждым окном.

## Что уже работает, а что нет (честно)

Работает — браузерный цефалон `http://127.0.0.1:5080/`:
голографический куб-решетка в духе Ордиса (оригинальная геометрия) со состояниями
`idle / thinking / speaking / working / denied / locked`, пульс ядра следует
громкости TTS, чат, запись голоса с микрофона, воспроизведение ответа,
WebSocket realtime-события, «Сейчас подумаю…» если ответ дольше 2,5 с.

НЕ сделано — десктоп-компаньон из обсуждения (always-on-top аватар поверх
рабочего стола, взгляд за окнами, game mode, сворачивание). Это будущий
компонент `TOLIK.Desktop`; текущий UI — веб-страница, а не «Скрепка 2.0».
Голос пока обычный TTS (нейронный Dmitry там, где сеть пускает к Microsoft,
иначе быстрый офлайн espeak) — кастомный «тактический» голос тоже впереди.

## Запуск (4 окна / 3 команды)

```powershell
cd C:\DEV\TOLIK_QWEN

# 1. Стек: postgres + voice + core (пароли уже лежат в .env)
docker compose --profile vps up -d

# 2. Модель на хосте (один раз; дальше держится теплой сама, keep_alive=60m)
ollama pull qwen2.5:3b-instruct

# 3. Регистрация устройства (один раз; перевыпуск безопасен — ключ переиспользуется)
dotnet run --project clients\TOLIK.Cli -- bootstrap --code TLK-BSTEP-2026-INIT
# токен ляжет в ~\.tolik\session-token

# 4. Агент Windows (отдельное окно, должно висеть)
$env:TOLIK_ALLOWED_ROOTS = 'C:\DEV\TOLIK_QWEN'
dotnet run --project src\TOLIK.Agent -- --core-url http://127.0.0.1:5080 `
  --token (Get-Content ~\.tolik\session-token) --heartbeat-sec 2

# 5. Интерфейс: открыть http://127.0.0.1:5080/ .
#    Токен подставляется сам (лаунчер включает TOLIK_DEV_AUTO_TOKEN, страница
#    забирает сессию с Core и показывает «Токен принят, агентов онлайн: N»).
#    Вручную: вставить токен из ~\.tolik\session-token в поле Bearer token.
```

Альтернатива без браузера: `dotnet run --project clients\TOLIK.Cli -- chat "текст"`
и `... -- listen` (голосовой цикл), токен подхватывается из `~\.tolik\session-token`
или `TOLIK_SESSION_TOKEN`.

## Первые фразы (smoke)

| Фраза | Что будет | Время |
|---|---|---|
| «открой калькулятор» (голосом или в чат) | глаз `thinking` → открывается `calc.exe` → ответ голосом | ~2 с |
| «привет, как дела?» | короткий ответ модели | ~0,6 с (прогрета) |
| «запомни: я люблю чай» | `Запомнил`, факт в памяти | ~1 с |
| «что я говорил про чай?» | ответ из памяти | ~1 с |
| «напомни через 2 минуты перерыв» | напоминание прилетит по WebSocket | — |
| Молчание > 2,5 с | глаз `thinking`, субтитр «Сейчас подумаю…» | заглушка UI |

## Проверка здоровья

```powershell
(Invoke-WebRequest http://127.0.0.1:5080/health).StatusCode          # 200
$h = @{ Authorization = "Bearer $((Get-Content ~\.tolik\session-token))" }
(Invoke-WebRequest http://127.0.0.1:5080/agents -Headers $h).Content  # status Connected
docker compose --profile vps ps                                       # 3× healthy/running
curl.exe -s -X POST -F "file=@.\artifacts\cosyvoice-dataset\clips\ref0001.wav" http://127.0.0.1:9660/stt/transcribe  # текст, не 500
```

> После КАЖДОЙ пересборки образов: смотреть ПОЛНЫЙ лог build (не хвост —
> тихие фейлы pip/apt уже дважды прятались), сверять время образа
> (`docker images … --format '{{.CreatedSince}}'`) и гонять smoke выше.
> Зависимости pip без пинов тянут breaking-мажоры (кейс: PyAV 19 vs
> faster-whisper 1.1) — новые пакеты только с пином версии.

## Troubleshooting

| Симптом | Причина / действие |
|---|---|
| `503 agent unavailable` / «агент недоступен» | не запущен агент (шаг 4); проверить `/agents` |
| Первый ответ модели ~5 с | холодный старт Ollama; дальше `keep_alive=60m` держит |
| Голос роботизированный | TLS к Microsoft TTS режется сетью → офлайн espeak; нейроголос вернется сам, где сеть пускает (breaker 10 мин); форсировать: `TOLIK_TTS_ENGINE=espeak` |
| Долгий голосовой цикл при первом вызове | edge-tts fail-fast ~4 с один раз, дальше ~0 с (cooldown) |
| Микрофон не просит доступ | нужен localhost (уже) или HTTPS; проверить разрешение в браузере |
| `401` на API | страница показывает точную причину; если «Токен не принят» — нажать Save Token заново или перезапустить через `TOLIK.bat` (токен подставится сам); запасной путь — bootstrap (шаг 3) |
| `Invoke-RestMethod` кажет «кракозябры» | баг отображения PowerShell, не данных; русский текст целый (проверено через `curl`/файл) |
| Агент не видит корень | `TOLIK_ALLOWED_ROOTS` должен покрывать рабочую папку |

## Остановка

```powershell
docker compose --profile vps down   # стек; данные Postgres сохраняются в volume
# окно агента закрыть Ctrl+C
```
