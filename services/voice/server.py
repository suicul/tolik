"""TOLIK Voice Service — Whisper STT + CosyVoice3 TTS.

Long-running AI service per AGENTS.md:
- models stay loaded between requests;
- communicates with TOLIK Core (.NET) over explicit HTTP interface;
- never logs secrets.

Engines:
- STT: `faster-whisper` (default, CPU-friendly, no torch) or `openai-whisper` (--engine openai-whisper).
- TTS: CosyVoice3 when TOLIK_COSYVOICE_ROOT is provided; otherwise /tts returns 503 (Core treats it as disabled).

Run:
    pip install fastapi uvicorn python-multipart faster-whisper
    python server.py --whisper-model base --port 9660
"""
import argparse
import io
import logging
import os
import tempfile
import time
from typing import Optional

from fastapi import FastAPI, File, Form, HTTPException, UploadFile
from fastapi.responses import Response, StreamingResponse
from pydantic import BaseModel

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
log = logging.getLogger("tolik.voice")

app = FastAPI(title="TOLIK Voice Service", version="1.1.0")


class SttResult(BaseModel):
    text: str
    language: str
    durationSeconds: float
    model: str


class TtsRequest(BaseModel):
    text: str
    speaker: str = "tolik"
    speed: float = 1.0
    engine: str = "auto"


_state = {"stt": None, "stt_engine": None, "stt_name": None, "cosyvoice": None, "tts_engine": "edge-tts"}


def load_stt_faster_whisper(model_name: str, device: Optional[str] = None):
    from faster_whisper import WhisperModel  # type: ignore

    # ctranslate2 reports a CUDA device even when the CUDA runtime DLLs (cublas) are
    # absent — probing it crashes transcription. Default to CPU (int8): fast enough
    # for short voice commands; pass --device cuda explicitly when the runtime is set up.
    resolved = device or "cpu"
    compute = "float16" if resolved == "cuda" else "int8"
    log.info("Loading faster-whisper model=%s device=%s compute=%s", model_name, resolved, compute)
    model = WhisperModel(model_name, device=resolved, compute_type=compute)
    _state["stt"] = ("faster-whisper", model)
    _state["stt_engine"] = "faster-whisper"
    _state["stt_name"] = model_name
    return model


def _cuda_available() -> bool:
    try:
        import ctranslate2  # type: ignore

        return ctranslate2.get_cuda_device_count() > 0
    except Exception:  # noqa: BLE001
        return False


def load_stt_openai_whisper(model_name: str):
    import torch
    import whisper

    device = "cuda" if torch.cuda.is_available() else "cpu"
    log.info("Loading openai-whisper model=%s device=%s", model_name, device)
    model = whisper.load_model(model_name, device=device)
    _state["stt"] = ("openai-whisper", model)
    _state["stt_engine"] = "openai-whisper"
    _state["stt_name"] = model_name
    return model


def load_stt(engine: str, model_name: str, device: Optional[str] = None):
    if engine == "faster-whisper":
        load_stt_faster_whisper(model_name, device)
    else:
        load_stt_openai_whisper(model_name)


def load_cosyvoice(root: str):
    """Load CosyVoice3 runtime when available; degrade gracefully otherwise."""
    try:
        from cosyvoice.cli.cosyvoice import CosyVoice2  # type: ignore

        _state["cosyvoice"] = CosyVoice2(root)
        log.info("CosyVoice loaded from %s", root)
    except Exception as ex:  # noqa: BLE001 — optional dependency
        log.warning("CosyVoice unavailable: %s", ex)
        _state["cosyvoice"] = None


@app.get("/health")
def health():
    return {
        "status": "Healthy",
        "sttEngine": _state["stt_engine"],
        "whisper": _state["stt_name"],
        "ttsEngine": _state["tts_engine"],
        "cosyvoice": _state["cosyvoice"] is not None,
        "speaker": _speaker_loaded(),
        "cuda": _cuda_available(),
    }


def _speaker_loaded() -> bool:
    try:
        import speaker_verify

        return speaker_verify.is_loaded()
    except Exception:  # noqa: BLE001
        return False


class SpeakerEmbedding(BaseModel):
    embedding: list[float]
    dim: int
    model: str


@app.post("/voice/speaker/embed", response_model=SpeakerEmbedding)
async def speaker_embed(file: UploadFile = File(...)):
    """Extract a speaker embedding from a voice sample (16 kHz mono expected;
    anything else is resampled). Rejects clips under 1s. 503 when disabled."""
    import speaker_verify

    if not _speaker_loaded():
        raise HTTPException(status_code=503, detail="speaker_not_configured")
    audio = await file.read()
    if len(audio) == 0:
        raise HTTPException(status_code=400, detail="empty_audio")
    if len(audio) > 10 * 1024 * 1024:
        raise HTTPException(status_code=400, detail="audio_too_large")
    try:
        import dsp_cephalon

        samples, _ = dsp_cephalon.decode_to_mono(audio, target_sr=16000)
    except Exception:
        raise HTTPException(status_code=400, detail="undecodable_audio")
    embedding = speaker_verify.embed_samples(samples)
    if embedding is None:
        raise HTTPException(status_code=422, detail="audio_too_short")
    return SpeakerEmbedding(embedding=embedding, dim=len(embedding), model="campplus")


@app.post("/stt/transcribe", response_model=SttResult)
async def transcribe(file: UploadFile = File(...), language: str = Form("ru")):
    engine, model = _state["stt"] if _state["stt"] else (None, None)
    if model is None:
        raise HTTPException(status_code=503, detail="whisper_not_loaded")
    audio = await file.read()
    if len(audio) == 0:
        raise HTTPException(status_code=400, detail="empty_audio")
    # Optional per-request language (dataset transcription for non-Russian audio);
    # default "ru" preserves live voice behavior. Whisper validates the code.
    language = (language or "ru").strip().lower() or "ru"

    started = time.time()
    if engine == "faster-whisper":
        # faster-whisper needs a path; write to a temp file with the original extension.
        suffix = os.path.splitext(file.filename or "audio.wav")[1] or ".wav"
        with tempfile.NamedTemporaryFile(suffix=suffix, delete=False) as temp:
            temp.write(audio)
            temp_path = temp.name
        try:
            segments, info = model.transcribe(temp_path, language=language, vad_filter=True)
            text = " ".join(segment.text.strip() for segment in segments).strip()
            duration = float(getattr(info, "duration", 0.0) or 0.0)
        finally:
            try:
                os.unlink(temp_path)
            except OSError:
                pass
    else:
        result = model.transcribe(audio, language=language, fp16=True)
        text = (result.get("text") or "").strip()
        duration = float(result.get("duration", 0.0) or 0.0)

    log.info("Transcribed %d bytes in %.2fs -> %d chars", len(audio), time.time() - started, len(text))
    return SttResult(
        text=text,
        language=language,
        durationSeconds=duration,
        model=_state["stt_name"] or "unknown",
    )


@app.post("/tts/synthesize-stream")
async def synthesize_stream(request: TtsRequest):
    """Streaming TTS (voice-runtime-v2.md 0.5/M4): edge-tts mp3 chunks are served as
    they arrive (chunked transfer) — the client plays chunk-by-chunk instead of waiting
    for the full audio. Non-streaming /tts/synthesize stays the fallback.
    Routing: quality/XTTS → blob via a single chunk; fast/auto → edge streaming."""
    log.info("TTS stream requested: %d chars engine=%s", len(request.text or ""), request.engine)
    text = (request.text or "").strip()
    if not text:
        raise HTTPException(status_code=400, detail="empty_text")
    engine = (request.engine or "auto").strip().lower() or "auto"

    async def single_chunk(data: bytes):
        yield data

    xtts_endpoint = os.getenv("TOLIK_XTTS_ENDPOINT", "").strip()
    if engine == "quality":
        # XTTS: no server-side streaming (blob) — serve as one chunk; on failure 503.
        if not xtts_endpoint:
            raise HTTPException(status_code=503, detail="quality_tts_not_configured")
        response = _synthesize_xtts(xtts_endpoint, text)
        return StreamingResponse(single_chunk(response.body), media_type="audio/wav")

    if engine == "auto" and xtts_endpoint:
        # auto: the chain default (XTTS first) served as one chunk; edge streaming on failure.
        try:
            response = _synthesize_xtts(xtts_endpoint, text)
            return StreamingResponse(single_chunk(response.body), media_type="audio/wav")
        except Exception as ex:  # noqa: BLE001 — fall through to edge streaming
            log.warning("XTTS sidecar failed, falling back to edge streaming: %s", ex)

    if _state["tts_engine"] != "edge-tts":
        raise HTTPException(status_code=503, detail="tts_not_configured")
    if time.time() < _state.get("edge_cooldown_until", 0.0):
        log.info("edge-tts cooling down after failures, offline TTS (single chunk)")
        offline = _synthesize_offline(text)
        if not offline:
            raise HTTPException(status_code=503, detail="tts_offline_failed")
        return StreamingResponse(single_chunk(offline), media_type="audio/wav")

    voice = _edge_voice(request.speaker)

    async def edge_chunks():
        # The same network bound as the non-streaming path: a stalled Microsoft
        # connection must not hang the stream forever (observed: 60s+ with no error).
        # RATE LIMIT (observed 04.10, matrix test): Microsoft throttles ~1 request per
        # 4-6s — rapid requests fail with NoAudioReceived in an alternating pattern.
        # Retry once after a 3s backoff (only when nothing was yielded — a mid-stream
        # failure must not duplicate already-played audio), then the offline fallback.
        import asyncio
        import edge_tts

        async def bounded_stream():
            communicate = edge_tts.Communicate(text, voice)
            async for chunk in communicate.stream():
                if chunk["type"] == "audio":
                    yield chunk["data"]
        got_audio = False
        for attempt in range(2):
            try:
                async with asyncio.timeout(12):
                    async for chunk in bounded_stream():
                        got_audio = True
                        yield chunk
                if got_audio:
                    return
            except Exception as ex:  # noqa: BLE001 — network TTS can fail many ways
                if got_audio:
                    return  # mid-stream failure: partial audio already played, no duplicate
                log.warning("edge-tts stream attempt %d failed: %s", attempt + 1, ex)
            if attempt == 0:
                await asyncio.sleep(3)
        log.warning("edge-tts streaming cooling down for 10 minutes after retries")
        _state["edge_cooldown_until"] = time.time() + 600.0
        offline = _synthesize_offline(text)  # Response; raises 503 if no offline engine
        if offline and offline.body:
            yield offline.body

    return StreamingResponse(edge_chunks(), media_type="audio/mpeg")


def _edge_voice(speaker: str) -> str:
    # Map short speaker names to full edge-tts voice ids; default = male Russian Dmitry.
    voices = {
        "tolik": "ru-RU-DmitryNeural",
        "dmitry": "ru-RU-DmitryNeural",
        "pavel": "ru-RU-PavelNeural",
        "male": "ru-RU-DmitryNeural",
        "female": "ru-RU-SvetlanaNeural",
    }
    return voices.get((speaker or "").lower(), "ru-RU-DmitryNeural")


@app.post("/tts/synthesize")
def synthesize(request: TtsRequest):
    log.info("TTS synthesize requested: %d chars engine=%s", len(request.text or ""), request.engine)
    text = (request.text or "").strip()
    if not text:
        raise HTTPException(status_code=400, detail="empty_text")

    # Routing (voice-runtime-v2.md 0.4): fast=edge-tts only (interactive), quality=XTTS
    # only (expressive/long), auto=the existing chain (XTTS -> edge -> offline).
    # The chain remains the fallback for quality failures — speech never breaks.
    engine = (request.engine or "auto").strip().lower() or "auto"
    if engine == "quality":
        xtts_endpoint = os.getenv("TOLIK_XTTS_ENDPOINT", "").strip()
        if not xtts_endpoint:
            raise HTTPException(status_code=503, detail="quality_tts_not_configured")
        return _synthesize_xtts(xtts_endpoint, text)

    # Primary engine: XTTS sidecar when configured (local neural voice, no
    # network filtering issues), then edge-tts, then offline fallback.
    if engine == "auto":
        xtts_endpoint = os.getenv("TOLIK_XTTS_ENDPOINT", "").strip()
        if xtts_endpoint:
            try:
                return _synthesize_xtts(xtts_endpoint, text)
            except Exception as ex:  # noqa: BLE001 — fall through to the regular chain
                log.warning("XTTS sidecar failed, falling back: %s", ex)
    if _state["tts_engine"] == "edge-tts":
        forced = os.getenv("TOLIK_TTS_ENGINE", "auto").strip().lower()
        if forced == "espeak":
            log.info("TTS engine forced to offline (TOLIK_TTS_ENGINE=espeak)")
            return _apply_dsp(_synthesize_offline(text), "offline espeak")
        if time.time() < _state.get("edge_cooldown_until", 0.0):
            log.info("edge-tts cooling down after failures, using offline TTS")
            return _apply_dsp(_synthesize_offline(text), "offline espeak")
        return _apply_dsp(_synthesize_edge(text, request.speaker), "edge-tts")

    cosy = _state["cosyvoice"]
    if cosy is None:
        raise HTTPException(status_code=503, detail="tts_not_configured")
    chunks = []
    for output in cosy.inference_sft(text, request.speaker, stream=False):
        chunks.append(output["tts_speech"])
    if not chunks:
        raise HTTPException(status_code=500, detail="tts_failed")
    import torch

    speech = torch.cat(chunks, dim=-1)
    import torchaudio

    buffer = io.BytesIO()
    torchaudio.save(buffer, speech, cosy.sample_rate, format="wav")
    return Response(content=buffer.getvalue(), media_type="audio/wav")


def _synthesize_espeak(text: str) -> Optional[bytes]:
    """Linux espeak fallback (offline, free, always available)."""
    import subprocess
    import tempfile as tempfile_module

    with tempfile_module.NamedTemporaryFile(suffix=".wav", delete=False) as temp:
        wav_path = temp.name
    try:
        # espeak-ng with Russian voice, male variant, moderate speed
        result = subprocess.run(
            ["espeak", "-v", "ru+m3", "-s", "160", "-w", wav_path, text],
            capture_output=True,
            timeout=30,
        )
        if result.returncode != 0 or not os.path.exists(wav_path):
            log.warning("espeak fallback failed: %s", result.stderr[-400:] if result.stderr else "unknown")
            return None
        with open(wav_path, "rb") as handle:
            data = handle.read()
        return data if data else None
    except Exception as ex:  # noqa: BLE001
        log.warning("espeak fallback error: %s", ex)
        return None
    finally:
        try:
            os.unlink(wav_path)
        except OSError:
            pass


def _synthesize_edge(text: str, speaker: str) -> Response:
    import asyncio
    import time as time_module

    import edge_tts

    # Map short speaker names to full edge-tts voice ids; default = male Russian Dmitry.
    voices = {
        "tolik": "ru-RU-DmitryNeural",
        "dmitry": "ru-RU-DmitryNeural",
        "pavel": "ru-RU-PavelNeural",
        "male": "ru-RU-DmitryNeural",
        "female": "ru-RU-SvetlanaNeural",
    }
    voice = voices.get((speaker or "").lower(), "ru-RU-DmitryNeural")

    async def run_once() -> bytes:
        # Hard network bound per attempt: edge-tts can hang indefinitely on
        # stalled Microsoft connections (observed: 60s+ with no error, no audio).
        async def stream_with_timeout() -> bytes:
            communicate = edge_tts.Communicate(text, voice)
            buffer = io.BytesIO()
            async for chunk in communicate.stream():
                if chunk["type"] == "audio":
                    buffer.write(chunk["data"])
            return buffer.getvalue()
        return await asyncio.wait_for(stream_with_timeout(), timeout=6)

    # edge-tts can flap (token/DRM drift, rate limits, filtered networks): 2 fast
    # attempts, then circuit-break for 10 minutes so later turns stay fast.
    last_error: Optional[Exception] = None
    for attempt in range(2):
        try:
            audio = asyncio.run(run_once())
            if audio:
                log.info("edge-tts synthesized %d bytes on attempt %d", len(audio), attempt + 1)
                # edge-tts streams mp3; declare it so the client writes the right extension.
                return Response(content=audio, media_type="audio/mpeg")
        except Exception as ex:  # noqa: BLE001 — network TTS can fail many ways
            last_error = ex
            log.warning("edge-tts attempt %d failed: %s", attempt + 1, ex)
            # RATE LIMIT (observed 04.10): ~1 request per 4-6s — a fast retry hits the
            # same throttle; back off before the next attempt.
            time_module.sleep(3)
    log.warning("edge-tts cooling down for 10 minutes: %s", last_error)
    _state["edge_cooldown_until"] = time_module.time() + 600.0
    return _synthesize_offline(text)


def _synthesize_xtts(endpoint: str, text: str) -> Response:
    """Local XTTS sidecar (opt-in GPU voice). Raises on any failure — the caller
    falls back to edge-tts/offline so speech never breaks (§41)."""
    import json
    import urllib.request

    payload = json.dumps({"text": text, "language": "ru"}).encode("utf-8")
    request = urllib.request.Request(
        endpoint.rstrip("/") + "/tts", data=payload,
        headers={"Content-Type": "application/json"}, method="POST")
    with urllib.request.urlopen(request, timeout=60) as response:
        audio = response.read()
    if not audio:
        raise RuntimeError("empty XTTS response")
    log.info("XTTS sidecar synthesized %d bytes", len(audio))
    return Response(content=audio, media_type="audio/wav")


def _apply_dsp(response: Response, engine_name: str) -> Response:
    """Cephalon character filter (TOLIK_VOICE_DSP=cephalon, default off).

    Runs after synthesis on any engine output so the voice character does not
    depend on the backend. Never breaks synthesis: any DSP failure returns the
    original audio untouched.
    """
    mode = os.getenv("TOLIK_VOICE_DSP", "off").strip().lower()
    if mode != "cephalon":
        return response
    try:
        import dsp_cephalon

        try:
            glitch = float(os.getenv("TOLIK_VOICE_GLITCH", "0.18"))
        except ValueError:
            glitch = 0.18
        processed = dsp_cephalon.process_request(response.body, glitch_prob=min(max(glitch, 0.0), 1.0))
        log.info("cephalon DSP applied to %s output (%d -> %d bytes)", engine_name, len(response.body), len(processed))
        return Response(content=processed, media_type="audio/wav")
    except Exception as ex:  # noqa: BLE001 — character filter must not fail speech
        log.warning("cephalon DSP failed, returning raw %s audio: %s", engine_name, ex)
        return response


def _synthesize_offline(text: str) -> Response:
    """Offline fallback: Windows SAPI (System.Speech) or Linux espeak — always available.
    Guarantees TOLIK can speak even without internet (§41: reliability first)."""
    if os.name == "nt":
        sapi = _synthesize_sapi(text)
        if sapi:
            return Response(content=sapi, media_type="audio/wav")
    else:
        espeak_audio = _synthesize_espeak(text)
        if espeak_audio:
            return Response(content=espeak_audio, media_type="audio/wav")
    raise HTTPException(status_code=503, detail="tts_failed: offline engine unavailable")


def _synthesize_sapi(text: str) -> Optional[bytes]:
    """Windows SAPI fallback via PowerShell System.Speech (offline, free)."""
    if os.name != "nt":
        return None
    import subprocess
    import tempfile as tempfile_module

    with tempfile_module.NamedTemporaryFile(suffix=".wav", delete=False) as temp:
        wav_path = temp.name
    try:
        script = (
            "Add-Type -AssemblyName System.Speech;"
            "$s = New-Object System.Speech.Synthesis.SpeechSynthesizer;"
            f"$s.SetOutputToWaveFile('{wav_path}');"
            "$s.Speak([Console]::In.ReadToEnd());"
            "$s.Dispose()"
        )
        result = subprocess.run(
            ["powershell", "-NoProfile", "-NonInteractive", "-Command", script],
            input=text,
            capture_output=True,
            text=True,
            timeout=30,
        )
        if result.returncode != 0 or not os.path.exists(wav_path):
            log.warning("SAPI fallback failed: %s", result.stderr[-400:] if result.stderr else "unknown")
            return None
        with open(wav_path, "rb") as handle:
            data = handle.read()
        return data if data else None
    except Exception as ex:  # noqa: BLE001
        log.warning("SAPI fallback error: %s", ex)
        return None
    finally:
        try:
            os.unlink(wav_path)
        except OSError:
            pass


def main():
    parser = argparse.ArgumentParser(description="TOLIK Voice Service")
    parser.add_argument("--port", type=int, default=int(os.environ.get("TOLIK_VOICE_PORT", "9660")))
    parser.add_argument("--host", type=str, default="0.0.0.0")
    parser.add_argument("--engine", type=str, default=os.environ.get("TOLIK_STT_ENGINE", "faster-whisper"),
                        choices=["faster-whisper", "openai-whisper"])
    parser.add_argument("--whisper-model", type=str, default=os.environ.get("TOLIK_WHISPER_MODEL", "base"))
    parser.add_argument("--device", type=str, default=os.environ.get("TOLIK_STT_DEVICE", "cpu"),
                        choices=["cpu", "cuda"], help="cpu works everywhere; cuda needs CUDA runtime DLLs")
    parser.add_argument("--cosyvoice-root", type=str, default=os.environ.get("TOLIK_COSYVOICE_ROOT", ""))
    args = parser.parse_args()

    load_stt(args.engine, args.whisper_model, args.device)
    if args.cosyvoice_root:
        load_cosyvoice(args.cosyvoice_root)

    # Speaker verification (CampPlus ONNX, CPU): env path or bundled model copy.
    # Absent/unloadable model only disables verification — never STT/TTS.
    try:
        import speaker_verify

        speaker_verify.load(os.environ.get(
            "TOLIK_CAMPPLUS_PATH", "/app/models/campplus.onnx"))
    except Exception as ex:  # noqa: BLE001 — optional capability
        log.warning("Speaker verification unavailable: %s", ex)

    # Warm-up: the first real transcribe after a cold start pays model init (~1-2s);
    # pre-running a silent sample moves that cost out of the user's first request.
    try:
        import wave as wave_module

        with tempfile.NamedTemporaryFile(suffix=".wav", delete=False) as silence:
            with wave_module.open(silence.name, "wb") as wav_writer:
                wav_writer.setnchannels(1)
                wav_writer.setsampwidth(2)
                wav_writer.setframerate(16000)
                wav_writer.writeframes(b"\x00\x00" * 16000)
            silence_path = silence.name
        if _state["stt"] and _state["stt"][0] == "faster-whisper":
            _state["stt"][1].transcribe(silence_path, language="ru", vad_filter=True)
            log.info("Whisper warm-up complete.")
        os.unlink(silence_path)
    except Exception as ex:  # noqa: BLE001 — warm-up must never block startup
        log.warning("Whisper warm-up skipped: %s", ex)

    import uvicorn

    uvicorn.run(app, host=args.host, port=args.port, log_level="info")


if __name__ == "__main__":
    main()
