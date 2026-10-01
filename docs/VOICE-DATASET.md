# TOLIK voice dataset pipeline (§14)

Цель: локальный CosyVoice-клон оригинального голоса в духе Ордиса
(вдохновение, не 1:1 копия — AGENTS.md §14). Все скрипты запускаются через
PowerShell 7 (`C:\Program Files\PowerShell\7\pwsh.exe -File <script>`).

## Стадии

| # | Скрипт | Что делает | Статус в манифесте |
|---|--------|-----------|-------------------|
| 1 | `scripts/prepare-cosyvoice-dataset.ps1` | `voice_reference/*` → mono 24 kHz wav + манифест | `needs_split` (записи > 2 мин) / `needs_transcript` |
| 2 | `scripts/split-audio-silence.ps1` | нарезка по тишине (`silencedetect`), клипы 1–20 с | `needs_transcript` |
| 3 | `scripts/transcribe-dataset.ps1` | whisper через voice-сервис (`/stt/transcribe`, curl.exe multipart) → `datasets/transcripts/manifest.jsonl` | — (JSONL отдельно) |
| 4 | `scripts/apply-whisper-transcripts.ps1` | тексты в манифест + `review.tsv` для ревью | `needs_review` |
| 5 | **ручное ревью** | правка `review.tsv` (whisper галлюцинирует на шумных клипах) | — |
| 6 | `scripts/apply-cosyvoice-transcripts.ps1` | проверенные строки → манифест | `transcribed` |
| 7 | `scripts/build-cosyvoice-manifests.ps1` | train/dev (`wav.scp`, `text`, `utt2spk`); жёсткий гейт: только `transcribed` | — |
| 8 | fine-tune CosyVoice3 (`CosyVoice3/`, Fun-CosyVoice3-0.5B) | обучение, GPU RTX 3060 12 ГБ — только по явному решению владельца | — |

## Текущее состояние (29.09.2026)

- Источник: 1 ч 22 м чистых реплик Ордиса (`voice_reference/`, один mp3).
- 985 клипов (1–13 с, в среднем 4 с), манифест `artifacts/cosyvoice-dataset/manifest.csv`.
- v1 транскриптов (whisper base, `language="ru"` — voice-сервис форсил русский)
  дал 674/983 строк с кириллическим супом («Городия», «Оперитор»): выброшены,
  бэкап `manifest.ru-soup.csv`.
- v2: `tts-dataset-generator` (инструмент пользователя, код не копируем в репо),
  whisper **medium** + `language=en`, GPU RTX 3060 — 985 файлов за ~15 мин.
  Кириллицы: 0; 1 `[WHISPER_ERROR]`; 2 коротких текста.
- Причина v1 зафиксирована кодом: `/stt/transcribe` теперь принимает
  опциональный `language` (дефолт `ru`, живое поведение не меняется).
- Все строки — `needs_review`: **требуется ревью** `review.tsv` перед обучением.

## Гигиена

- `artifacts/`, `datasets/`, `voice_reference/`, `*.pt`, `*.onnx` в `.gitignore` —
  бинарники и референсы в git не попадают.
- Гейт ревью обязателен: `build-cosyvoice-manifests.ps1` падает на любой
  строке без статуса `transcribed` (AGENTS.md §49: молчаливых фактов нет).

## Нейроголос (XTTS-v2, 29.09.2026)

- Сайдкар `services/xtts/` (GPU, профиль `gpu`): zero-shot клонирование,
  промпт `artifacts/cosyvoice-dataset/prompt_ordis.wav` (11,5 с, 3 клипа).
- Voice-сервис идёт в сайдкар первым при `TOLIK_XTTS_ENDPOINT`, иначе
  обычная цепочка (речь не ломается никогда).
- Проверка чёткости (STT по собственному синтезу): «все системы в норме»,
  «Ордис завершил диагностику» распознаны; остаток — имена собственные
  (Цетус) → кандидаты в словарь нормализации.
- Демо: `artifacts/voice-demos/xtts_ordis_*.wav`. Включение — только после
  прослушивания владельцем.
- **Включено 29.09.2026** (`TOLIK_XTTS_ENDPOINT=http://xtts:9670` в `.env`):
  полный тракт проверен живьём — синтез через сайдкар, STT-контроль дословный.
