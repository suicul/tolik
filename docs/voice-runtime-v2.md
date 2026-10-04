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

- **M1 — TTS benchmark** (scripts/benchmark-tts.py): TTFA/total/RTF для edge-tts/XTTS/espeak
  на русском наборе (ACK/обычный/длинный/числа/термины/имена/эмоция), cold/warm,
  RAM/VRAM (XTTS). Воспроизводимо, отчёт в docs. Не фальсифицировать.
- **M2 — backend abstraction**: TtsCapabilities (SupportsStreaming/SupportsCancellation/
  SupportsRussian/EstimatedLatency/QualityClass) + TtsBackendKind на существующем ITtsProvider.
- **M3 — TTS routing**: FAST (edge-tts)/QUALITY (XTTS)/AUTO (по классу ответа);
  существующая цепочка = fallback. Не удалять рабочий backend.
- **M4 — streaming**: server.py /tts/synthesize-stream (edge_tts.stream() chunks → немедленно)
  + C# SynthesizeStreamAsync → IAsyncEnumerable<TtsChunk>; LLM streaming → sentence chunks.
  Non-streaming fallback сохраняется.
- **M5 — barge-in**: playback cancel (клиент/Desktop) + TTS token глубоко + live VAD детекция.
- **M6 — live VAD (Silero)**: threshold/min-speech/silence/pre/post-roll — TOLIK_VAD_* конфиг.
- **M7 — telemetry + prewarm**: VoiceTurnTelemetry (turn_start/vad/stt/llm/tts/playback
  тайминги, без приватного текста) + prewarm (edge-tts прогрев, XTTS warm) + cold/warm TTFA.
- **M8 — modes/game mute**: SILENT в VoiceRuntimePolicy + игра→mute связка.
- **0.9/0.10**: wake-word boundary (IWakeWordDetector, режимы always/wake/PTT — без фейка) +
  direct mic path (NAudio → VAD → STT, отдельный VoiceCaptureService).
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
