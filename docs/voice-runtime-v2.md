# TOLIK Voice Runtime V2 — M0 Audit (04.10.2026)

Полный аудит текущего voice pipeline по точкам директивы. Источник: чтение кода
(services/voice/server.py, TtsProvider.cs, CosyVoice3TtsProvider.cs, Program.cs
/voice/*, WindowsAgent play_sound) + live-состояние (tolik-voice-1/xtts-1 healthy).

## 1. Архитектура как есть

```text
Микрофон (браузер MediaRecorder / агент NAudio-capture — НЕ подключён)
  ↓ POST /stt/transcribe (multipart)
WhisperStt — faster-whisper CPU int8 (CUDA probe guard)
  + vad_filter=True — Silero VAD УЖЕ ВКЛЮЧЁН (POST-hoc на загруженном аудио!)
  ↓ JSON {text, language, durationSeconds, model}
SpeakerVerifier — CampPlus embed (/voice/speaker/embed), порог 0.45,
  мульти-семпл enroll до 5 клипов (mean unit vector)
  ↓ guest mode если < порога (unverified → guest-инструкция + voice.user.unverified)
IntentRouter — детерминированные интенты (голосовые простые интенты без модели)
  ↓
LLM — qwen3:4b local / cloud gpt (voice path: NumPredict 250, Think:false,
  NumCtx 8192, persona concise) — COMPLETE (не streaming)
  ↓ полный текст
Sanitizer (§22/T11) → TrimForSpeech (последнее полное предложение ~30с)
  ↓
POST /tts/synthesize — цепочка: XTTS sidecar (WAV, 60s) → edge-tts
  (ru-RU-DmitryNeural, mp3, 6s network bound, 2 попытки → circuit-break 10 мин)
  → espeak offline (ru+m3, WAV)
  + _apply_dsp (dsp_cephalon.py — «cephalon character filter», TOLIK_VOICE_DSP,
  default off) — post-processing УЖЕ СУЩЕСТВУЕТ
  ↓ ПОЛНЫЙ audio blob
HTTP 200 audio → КЛИЕНТ играет (браузер <audio> / CLI; NAudio playback только
  для soundboard play_sound — allowlist + rate limiter)
```

## 2. Что УЖЕ существует (открытия, меняющие план)

1. **edge-tts — готовый FAST backend**: streaming API (`edge_tts.Communicate.stream()`)
   УЖЕ используется внутри сервера, но chunks собираются в полный blob. Русские
   голоса Dmitry/Pavel/Svetlana. Резилентность построена: 6s network bound,
   2 попытки → circuit-break 10 мин → espeak fallback. TTFA ~200-500ms (не измерено — M1).
2. **ITtsProvider уже существует** (TtsProvider.cs) — abstraction (0.2) = расширение
   capability-декларациями, не новый интерфейс.
3. **VAD частично есть**: faster-whisper `vad_filter=True` (Silero, POST-hoc на
   загруженном аудио — тишину убирает). LIVE/streaming VAD — отсутствует (нужен barge-in).
4. **DSP cephalon filter уже существует** (dsp_cephalon.py, default off) — 0.16 частично готов.
5. **Cancellation глубокий уже частично**: /voice/turn token → TTS linked timeout +
   token; LLM token. Barge-in: playback отмена = клиент abort вручную.
6. **XTTS/CosyVoice — QUALITY backend'ы**: сохраняются. CosyVoice даже server-side
   `stream=False` (blob) — streaming для него = отдельная работа.
7. Голосовой путь уже короче чатового: NumPredict 250, Think:false, persona concise,
   TrimForSpeech. Проверить дозачистку markdown/JSON (0.12).

## 3. Отсутствует (gap'ы против директивы)

| Gap | Точка | Где |
|---|---|---|
| Streaming end-to-end | 0.5/0.6 | LLM complete → blob → play; edge-tts стримит внутри, но собирает blob |
| Barge-in | 0.7 | Нет live VAD-during-playback; отмена = клиент abort |
| Live VAD | 0.8 | Только POST-hoc vad_filter в transcribe |
| Direct mic → overlay | 0.10 | NAudio capture не подключён к Desktop/agent voice path |
| TTS routing FAST/QUALITY/AUTO | 0.4 | Порядок зашит: XTTS → edge → espeak (quality-first) |
| VoiceResponseClass | 0.11 | Нет классов ответа |
| Latency telemetry | 0.14 | Только coarse-логи |
| Prewarm | 0.13 | Whisper загружен при старте ✓; TTS не прогревается |
| Wake word | 0.9 | Нет boundary/backend |
| Game mute | 0.17 | VoiceRuntimePolicy есть; связки игра→mute нет |

## 4. План реализации M1-M8 + Phase 1

- **M1 — TTS benchmark** — **DONE 04.10.2026, измерено** (scripts/benchmark-tts.py, live):

  | Фраза | chain total | duration | RTF |
  |---|---|---|---|
  | ack (cold) | 2138 ms | 2.04s | 1.05 |
  | normal | 1037 ms | 4.36s | 0.24 |
  | long | 2226 ms | 9.28s | 0.24 |
  | numbers | 2010 ms | 8.49s | 0.24 |
  | technical | 1894 ms | 8.08s | 0.23 |
  | names | 1317 ms | 5.57s | 0.24 |
  | emotional | 1244 ms | 5.32s | 0.23 |
  | ack (xtts, cold) | **520 ms** | 2.22s | 0.23 |
  | normal (xtts, warm) | 944 ms | 3.89s | 0.24 |

  Выводы (честные): RTF 0.23-0.24 warm — быстрее реального времени ✓; cold chain
  2138ms (прогрев после простоя) vs XTTS direct cold 520ms — overhead цепочки ~1.6s
  на cold; **TTFA == total для blob API** (цель <200ms недостижима без streaming — M4);
  total turn = LLM (не измерено) + TTS ~1-2s — телеметрия M7 измерит LLM TTFT.
  espeak-only/edge-only замеры — per-engine server instances (follow-up).
- **M2 — backend abstraction**: TtsCapabilities (SupportsStreaming/SupportsCancellation/
  SupportsRussian/EstimatedLatency/QualityClass) + TtsBackendKind на существующем ITtsProvider.
- **M3 — TTS routing**: FAST (edge-tts)/QUALITY (XTTS)/AUTO (по классу ответа);
  существующая цепочка = fallback. Не удалять рабочий backend.
- **M4 — streaming** — **DONE 04.10.2026, измерено live** (commits 972867a/67ae182/c3516ff/21ad394):

  - M4a: server.py /tts/synthesize-stream (StreamingResponse, edge chunks, rate-limit
    retry, offline fallback) + C# SynthesizeStreamAsync; edge-tts 6.1.9→7.2.8 (403 fix).
  - M4b: SentenceChunker (Split + StreamingChunker, MinChunkLength 12) + LLM streaming
    (BuildStreaming stream:true — Ollama NDJSON / OpenAI SSE; CompleteStreamAsync
    IAsyncEnumerable<string>; ExtractStreamDelta — static, testable) + /voice/turn-stream
    SSE-эндпоинт: LLM deltas → sentence → TTS per-sentence (санитизация per-sentence —
    инструкции не озвучиваются) → delta/audio/done события; non-streaming fallback.

  | Метрика (live, нативный клиент, zero_shot_prompt.wav) | streaming | non-streaming |
  |---|---|---|
  | TTFT (первый delta) cold | 9602 ms | — |
  | TTFT (первый delta) warm | **1036 ms** | — |
  | TTFA (первое аудио) warm | **6037 ms** | 15600 ms (= весь turn) |
  | Total turn warm | 19677 ms | 15600 ms |
  | События | delta=250, audio=2, done=1 | 1 blob (3.2 MB) |

  Выводы (честные): TTFA warm **6.0s vs 15.6s = 2.6× быстрее первое аудио** — LLM
  стримит, первая озвученная фраза уходит до завершения генерации. Total стрима дольше
  (per-sentence TTS 2×944ms + токен-за-токеном LLM) — плата за ранний старт воспроизведения,
  исчезает при параллельном playback (M5). Cold TTFT 9.6s = загрузка модели Ollama после
  старта (не streaming overhead) — prewarm M7. Error-путь верифицирован: Ollama down →
  SSE {"type":"error","error":"model_unavailable"} (graceful, не 5xx). SSE пишется напрямую
  в Response.Body (Results.Stream Func-перегрузка недоступна в этой версии ASP.NET).
  266/266 тестов (+4 parser, +7 chunker).
- **M5 — barge-in** — **DONE 05.10.2026, verified live** (commit a7c25b4):
  VoiceTurnRegistry (Core): per-user in-flight turns — новый turn авто-отменяет
  предыдущий (barge-in), Complete/Cancel; CTS не диспозятся (без таймеров, dispose
  после Cancel гоняет linked-подписчиков). /voice/cancel: отмена СОБСТВЕННОГО
  in-flight turn'а (per-user scoping §7 — клиент не может отменить чужой), аудируется.
  /voice/turn-stream: register → linked token (registry CTS + RequestAborted) →
  LLM stream/TTS/SSE writes → finally Complete. TTS token глубоко: CosyVoice3TtsProvider
  уже проводит linked token через HTTP POST + read (без фикса).
  Live: cancel на 6s в середине стрима → {"cancelled":true}, SSE оборвался рано
  (audio=1, delta=64, без done vs полный delta=250/audio≈12/done=1) — LLM+TTS
  остановлены. 5 тестов реестра (cancel/no-turn/complete/barge-in/кросс-юзер
  независимость §7); 271/271 green. VAD-детекция речи во время playback → M6.
- **M6 — live VAD (Silero)** — **DONE 05.10.2026, verified live** (services/voice/server.py /vad/detect):
  Silero через уже установленный faster-whisper 1.2.1 (get_speech_timestamps +
  VadOptions) — без новых зависимостей. Аудио → 16kHz mono float32 (decode_audio) →
  сегменты речи в ms с pre/post-roll (TOLIK_VAD_PREROLL_MS/TOLIK_VAD_POSTROLL_MS,
  отдельные границы, speech_pad_ms=0 — без двойного пэддинга). TOLIK_VAD_* конфиг:
  THRESHOLD (0.5) / MIN_SPEECH_MS (250) / MIN_SILENCE_MS (100) / PREROLL_MS (0) /
  POSTROLL_MS (0). Фундамент для barge-in (речь во время playback) и STT pre-filter.
  Live-замеры: речь (4s wav) → hasSpeech=true, speechMs=2816, segments=[384→3200ms],
  латентность **122ms**; тишина (2s) → hasSpeech=false, **24ms**. Синтетический шум →
  нет сегментов (Silero отличает речь от шума). Audio не хранится (privacy §50).
- **M7 — telemetry + prewarm** — **DONE 05.10.2026, verified live** (commit 619a45f):
  VoiceTurnTelemetry (Core): per-turn стадии как structured events — turn_id, stage,
  elapsed_ms, outcome. НИКАКОГО приватного текста (§38) — только фиксированные литералы
  стадий и тайминги. Стадии: turn_start/stt_done/speaker_done/llm_first_delta/
  tts_first_audio + Complete(done|error|cancelled); cold/warm TTFA теперь измеримы из
  логов автоматически. Prewarm: ModelPrewarmService (core) — LLM ping на старте
  (NumPredict 1, литерал «ping», keep_alive держит тёплой) + TTS warm-up (voice service,
  короткая фраза через цепочку; edge-tts тратит ~1 запрос rate-бюджета на старте — ок).
  Live-замеры (первый turn после рестарта, раньше = cold 9.6s TTFT):
  Model prewarm done 4303ms (qwen3:4b) на старте; TTS warm-up 88652 bytes;
  первый turn: TTFT **3675ms** (было 9602ms), TTFA 7414ms; профиль:
  stt 478ms → speaker 313ms → llm+память 2805ms → предложение+TTS 3740ms.
  Playback-стадия — на клиенте (Desktop/VR), сервер не измеряет. 2 теста телеметрии;
  273/273 green.
- **M8 — modes/game mute** — **DONE 06.10.2026, verified live** (commit 4f8d411):
  VoiceRuntimeMode.Silent: TOLIK слушает и отвечает текстом, никогда не говорит
  (Stt on, Tts off); алиасы silent/тихий/тихо/тишина/молчи; IntentRouter: «тихий
  режим»/«молчи»/«тишина» → SwitchVoiceMode Silent. Game mute связка: heartbeat
  агента несёт имя foreground-процесса (GetForegroundWindow P/Invoke, READ_ONLY
  perception); Core матчит TOLIK_GAME_PROCESSES (case-insensitive, запятая) →
  VoiceRuntimeState.ApplyGamePresence: Normal ⇄ GamingAssist (слушать, ассистировать,
  не говорить в игру) и обратно; ручные режимы (Night/Silent/Gaming/ручной
  GamingAssist) НИКОГДА не переопределяются; ручной switch отменяет auto-связь.
  Имя процесса — perception, не authority (только голосовой режим, никогда
  авторизация); логируются переключения, не foreground.
  Live-верификация: TOLIK_GAME_PROCESSES=OpenCode → heartbeat `{"foreground":"OpenCode"}`
  (агент, каждые 10s, sent ✓) → **"Voice runtime mode auto-switched to GamingAssist
  (game detected)"** в core-логах ✓. Restore — тот же путь с gameRunning=false
  (юнит-проверен: 9 тестов авто-switch/restore/защита ручных режимов/идемпотентность);
  live-restore срабатывает при первом уходе фокуса (проверяемо в логах).
  Попутно чинен латентный баг: одиночные слова-команды («ночь»!) становились
  «открой X» → OpenApp — задокументированный алиас Night был недостижим.
  286/286 green. Настройка на live: TOLIK_GAME_PROCESSES пуст (связка выключена) —
  владелец задаст список своих игр.
- **0.9/0.10**: wake-word boundary (IWakeWordDetector, режимы always/wake/PTT — без фейка) +
  direct mic path (NAudio → VAD → STT, отдельный VoiceCaptureService).
- **0.9/0.10 — wake-word boundary + direct mic path** — **DONE 06.10.2026, verified live** (commits f89494a + 1d23299):
  0.9 (без фейка): VoiceActivationMode (Always/Wake/PushToTalk) + IWakeWordDetector
  (Contracts, dependency-free); резолвер: Wake без реального детектора → честный
  fallback в Always с видимым логом; PTT требует hotkey (Desktop).
  0.10: UtteranceSegmenter (Contracts, чистая логика: pre-roll 250ms, end-silence
  700ms, hard cap 10s) + VoiceCaptureService (агент): NAudio WaveInEvent →
  voice-service /vad/detect (M6) → сегментация → /voice/turn-stream SSE →
  текст в консоль + NAudio playback (WAV/MP3).
  **Live-фиксы от_owner-теста (Behringer UMC204HD + B2 Pro, вход 1):**
  - Дефолтное WaveIn-устройство была виртуалка «Steam Streaming Micro» (тишина!) →
    --voice-device N + печать списка устройств на старте. ASIO не нужен — winmm
    через стандартные эндпоинты UMC204HD работает (VAD-хит доказан).
  - Двухканальные интерфейсы требуют STEREO-захват + mixdown (L+R)/2: mono слышит
    только канал 1; probe 48k stereo → 44.1k → 16k mono; nearest-neighbor декимация.
  - NAudio StopRecording асинхронный: probe на одном инстансе + переиспользование =
    «Already recording» → probe одноразовый, боевой capture — свежий инстанс.
  - Эхо-цикл (TOLIK сам с собой говорил, живой отчёт владельца): пер-чанковый гейт
    пропускал TTS между предложениями → гейт на ВЕСЬ turn + хвост 400ms; verified:
    0 ложных VAD-хитов после ответа.
  - «Озвучивает все мысли» (отчёт владельца): MaxSpokenSentences=3 озвучивается,
    остальное — текстом.
  - ESC = barge-in: /voice/cancel (M5) + стоп playback; голосовой barge-in во время
    речи TOLIK требует AEC — честное ограничение, без фейка.
  - UTF-8 консоль. E2E verified live: колонки → мик Behringer → VAD (320ms) →
    utterance → turn → 3 предложения голосом + остальное текстом → тишина. 17 тестов
    (сегментация/резолвер); 303/303 green.
- **0.11/0.12**: VoiceResponseClass (ACK/SHORT/NORMAL/DETAILED/... — длина/TTS/streaming/interrupt)
  + voice LLM policy дозачистка (markdown/JSON в голос не попадают).
- **Phase 1 (M9-M13) — Desktop cephalon**: clients/TOLIK.Desktop (WPF) поверх стабильного
  runtime: overlay окно, глаз (cephalon.html reference), состояния от RealtimeEventHub
  (WebSocket, per-user privacy), speaking = audio amplitude → eye pulse, working = реальный
  TaskProgress arc, tray, boot animation, запуск из start-tolik.ps1.

## 5. Приоритет директивы

latency + responsiveness + interruptibility > naturalness. edge-tts (быстрый русский)
кандидат в FAST default после M1; XTTS/CosyVoice остаются QUALITY. Все performance
claims — только измеренные (M1/M7).
