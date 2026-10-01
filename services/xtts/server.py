# TOLIK XTTS Service — local neural TTS with voice cloning (AGENTS.md §4: specialized AI runtime).
# Coqui XTTS-v2: multilingual incl. Russian, zero-shot cloning from a prompt wav.
# Opt-in GPU sidecar (profile "gpu"); Core/voice-service route here only when configured.
#
#   TOLIK_XTTS_SPEAKER=/data/prompt.wav   prompt voice (Ordis clip, 6-30s clean speech)
#   POST /tts {"text": "...", "language": "ru"} -> audio/wav
import io
import logging
import os
import wave

from fastapi import FastAPI, HTTPException
from fastapi.responses import Response
from pydantic import BaseModel

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
log = logging.getLogger("tolik.xtts")

app = FastAPI(title="TOLIK XTTS Service", version="1.0.0")


class TtsRequest(BaseModel):
    text: str
    language: str = "ru"


_state = {"model": None, "speaker": os.environ.get("TOLIK_XTTS_SPEAKER", "/data/prompt.wav")}


@app.on_event("startup")
def load_model() -> None:
    import torch
    from TTS.api import TTS

    device = "cuda" if torch.cuda.is_available() else "cpu"
    log.info("Loading XTTS-v2 on %s (speaker prompt: %s)", device, _state["speaker"])
    if not os.path.exists(_state["speaker"]):
        raise RuntimeError(f"speaker prompt not found: {_state['speaker']}")
    _state["model"] = TTS("tts_models/multilingual/multi-dataset/xtts_v2", gpu=(device == "cuda"))
    log.info("XTTS-v2 ready")


@app.get("/health")
def health():
    import torch

    return {
        "status": "Healthy" if _state["model"] is not None else "Loading",
        "model": "xtts_v2",
        "cuda": torch.cuda.is_available(),
        "speaker": os.path.basename(_state["speaker"]),
    }


@app.post("/tts")
def synthesize(request: TtsRequest):
    if _state["model"] is None:
        raise HTTPException(status_code=503, detail="xtts_not_loaded")
    text = (request.text or "").strip()
    if not text:
        raise HTTPException(status_code=400, detail="empty_text")
    if len(text) > 2000:
        raise HTTPException(status_code=400, detail="text_too_long")
    language = (request.language or "ru").strip().lower() or "ru"
    try:
        pcm = _state["model"].tts(text, speaker_wav=_state["speaker"], language=language)
    except Exception as ex:  # noqa: BLE001 — model errors become 500, never leak stacks
        log.warning("XTTS synthesis failed: %s", ex)
        raise HTTPException(status_code=500, detail="xtts_failed")
    import numpy as np

    samples = (np.clip(np.asarray(pcm, dtype=np.float32), -1.0, 1.0) * 32767.0).astype(np.int16)
    buffer = io.BytesIO()
    with wave.open(buffer, "wb") as wav:
        wav.setnchannels(1)
        wav.setsampwidth(2)
        wav.setframerate(24000)
        wav.writeframes(samples.tobytes())
    return Response(content=buffer.getvalue(), media_type="audio/wav")


def main() -> None:
    import argparse

    import uvicorn

    parser = argparse.ArgumentParser(description="TOLIK XTTS Service")
    parser.add_argument("--port", type=int, default=int(os.environ.get("TOLIK_XTTS_PORT", "9670")))
    parser.add_argument("--host", type=str, default="0.0.0.0")
    args = parser.parse_args()
    uvicorn.run(app, host=args.host, port=args.port, log_level="info")


if __name__ == "__main__":
    main()
