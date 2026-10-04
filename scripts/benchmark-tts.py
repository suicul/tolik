"""TOLIK TTS Benchmark (docs/voice-runtime-v2.md M1).

Measures the REAL latency of the TTS backends on a standard Russian phrase set:
- the live voice service chain (POST :9660/tts/synthesize — the chain the user hears:
  XTTS sidecar -> edge-tts -> espeak),
- the XTTS sidecar directly (POST :9670/tts),
- the offline espeak path (via the live service is not switchable per-request — the
  script measures the live chain; espeak-only requires a local server instance).

Metrics per phrase per run: total ms (request -> full audio), audio bytes, estimated
audio duration (WAV header parse / mp3 bitrate estimate), real-time factor
(total / duration).

HONEST NOTES (no invented results):
- The current API returns the FULL audio blob: time-to-first-audio == total for this
  API shape. The TRUE TTFA requires the streaming API (M4) — this benchmark measures
  the end-to-end turn latency, which IS what the user experiences today.
- cold = the first request per process run; warm = the subsequent ones. Cold/warm are
  reported separately and never mixed.
- espeak-only and edge-only measurements need per-engine server instances — a follow-up
  (the script keeps the structure for them).

Run: python scripts/benchmark-tts.py [--voice-url http://127.0.0.1:9660] [--xtts-url http://127.0.0.1:9670]
"""
import argparse
import json
import struct
import sys
import time

import requests

PHRASES = [
    ("ack", "Готово."),
    ("normal", "Задача выполнена, отчёт сохранён в проекте."),
    ("long", "Я проверил репозиторий, нашёл причину сбоя сборки, исправил её и запустил все тесты повторно, чтобы убедиться, что ничего не сломалось."),
    ("numbers", "Обработано 128 файлов, найдено 3 ошибки, из них 2 критические и 1 некритическая."),
    ("technical", "Конфигурация PostgreSQL миграции добавлена в TOLIK.Infrastructure, схема применена без потери данных."),
    ("names", "Мария, Иван и Анастасия уже зарегистрированы в системе."),
    ("emotional", "Отлично, я так и знал! Это было абсолютно предсказуемо."),
]


def wav_duration(data: bytes):
    """Parses a RIFF/WAVE header; returns seconds or None."""
    if len(data) < 44 or data[:4] != b"RIFF" or data[8:12] != b"WAVE":
        return None
    try:
        fmt = struct.unpack_from("<HHIIHH", data, 20)
        byte_rate = fmt[3]
        # find the data chunk
        idx = 12
        while idx + 8 <= len(data):
            cid, size = struct.unpack_from("<4sI", data, idx)
            if cid == b"data":
                return size / byte_rate
            idx += 8 + size + (size % 2)
    except Exception:
        return None
    return None


def mp3_duration_estimate(data: bytes):
    """Crude estimate from bytes at ~48 kbps mono mp3 (edge-tts default 24k)."""
    return len(data) * 8.0 / 48000.0


def measure(url, phrase, is_wav_expected):
    started = time.perf_counter()
    try:
        r = requests.post(url, json={"text": phrase}, timeout=90)
    except Exception as ex:
        return {"error": f"{type(ex).__name__}: {ex}"[:120]}
    total_ms = (time.perf_counter() - started) * 1000
    if r.status_code != 200:
        return {"error": f"HTTP {r.status_code}", "total_ms": round(total_ms)}
    audio = r.content
    media = r.headers.get("Content-Type", "?")
    duration = wav_duration(audio) if "wav" in media else mp3_duration_estimate(audio)
    rtf = round(total_ms / 1000 / duration, 2) if duration and duration > 0 else None
    return {
        "total_ms": round(total_ms),
        "audio_bytes": len(audio),
        "media": media.split(";")[0],
        "duration_s": round(duration, 2) if duration else None,
        "rtf": rtf,
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--voice-url", default="http://127.0.0.1:9660")
    ap.add_argument("--xtts-url", default="http://127.0.0.1:9670")
    args = ap.parse_args()

    results = {"chain": [], "xtts": []}

    # The live chain: cold (first) + warm.
    for i, (name, phrase) in enumerate(PHRASES):
        r = measure(f"{args.voice_url}/tts/synthesize", phrase, False)
        r["phrase"] = name
        r["run"] = "cold" if i == 0 else "warm"
        results["chain"].append(r)

    # XTTS sidecar directly (cold + warm on the first two phrases).
    for i, (name, phrase) in enumerate(PHRASES[:2]):
        r = measure(f"{args.xtts_url}/tts", phrase, True)
        r["phrase"] = name
        r["run"] = "cold" if i == 0 else "warm"
        results["xtts"].append(r)

    print(json.dumps(results, ensure_ascii=False, indent=2))

    # Summary table.
    print("\n=== SUMMARY (total_ms | duration_s | rtf) ===")
    for name, phrase in PHRASES:
        row = next((r for r in results["chain"] if r["phrase"] == name), {})
        print(f"{name:10} chain  {row.get('total_ms','?'):>6} ms | {row.get('duration_s','?')} s | rtf={row.get('rtf','?')}")
    for r in results["xtts"]:
        print(f"{r['phrase']:10} xtts   {r.get('total_ms','?'):>6} ms | {r.get('duration_s','?')} s | rtf={r.get('rtf','?')}")


if __name__ == "__main__":
    sys.exit(main())
