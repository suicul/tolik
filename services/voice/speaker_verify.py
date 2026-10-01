"""Speaker verification embeddings (AGENTS.md §13: device != user != speaker).

CampPlus ONNX (CPU, onnxruntime) with kaldi-native-fbank features — the same
extractor CosyVoice3 uses, so no new model downloads and no torch. Stateless:
this module only extracts embeddings; enrollment storage and accept/reject
decisions live in TOLIK Core (Postgres), which also owns the audit trail.

Graceful degradation: if the model file is absent or dependencies fail to
import, load() returns False and the /voice/speaker/* endpoints answer 503
(speaker_not_configured). Core treats that as "verification disabled".
Model weights: Fun-CosyVoice3 CampPlus, local personal use only.
"""

import logging
import os

log = logging.getLogger("tolik.voice")

MODEL_DIM = 192

_state = {"session": None, "input_name": "", "model": "campplus"}


def load(model_path: str) -> bool:
    """Load the CampPlus ONNX session. Returns False (never raises) when unavailable."""
    if not model_path or not os.path.exists(model_path):
        log.warning("Speaker model not found at %s — verification disabled", model_path)
        return False
    try:
        import onnxruntime

        options = onnxruntime.SessionOptions()
        options.intra_op_num_threads = max(1, (os.cpu_count() or 4) // 2)
        session = onnxruntime.InferenceSession(model_path, sess_options=options, providers=["CPUExecutionProvider"])
        _state["session"] = session
        _state["input_name"] = session.get_inputs()[0].name
        log.info("Speaker model loaded: %s (dim check on first embed)", model_path)
        return True
    except Exception as ex:  # noqa: BLE001 — optional capability
        log.warning("Speaker model unavailable: %s", ex)
        _state["session"] = None
        return False


def is_loaded() -> bool:
    return _state["session"] is not None


def embed_samples(samples_16k_mono) -> list[float] | None:
    """Extract a speaker embedding from 16 kHz mono float32 samples.

    Kaldi-compatible 80-bin fbank (dither=0), utterance mean normalization —
    the exact CampPlus training recipe. Returns None for too-short audio.
    """
    if not is_loaded():
        return None
    try:
        import kaldi_native_fbank as knf
        import numpy as np

        samples = np.asarray(samples_16k_mono, dtype=np.float32).flatten()
        if len(samples) < 16000:
            return None
        opts = knf.FbankOptions()
        opts.frame_opts.dither = 0.0
        opts.frame_opts.samp_freq = 16000
        opts.mel_opts.num_bins = 80
        online = knf.OnlineFbank(opts)
        online.accept_waveform(16000, samples)
        online.input_finished()
        frames = np.stack([online.get_frame(i) for i in range(online.num_frames_ready)], axis=0)
        feat = frames - frames.mean(axis=0, keepdims=True)
        embedding = _state["session"].run(None, {_state["input_name"]: feat[np.newaxis, :, :].astype(np.float32)})[0]
        vector = np.asarray(embedding, dtype=np.float64).flatten()
        norm = float(np.linalg.norm(vector))
        if norm <= 0.0:
            return None
        return (vector / norm).tolist()
    except Exception as ex:  # noqa: BLE001 — extraction must not fail requests
        log.warning("Speaker embedding failed: %s", ex)
        return None


def cosine(a: list[float], b: list[float]) -> float:
    """Cosine similarity of two unit embeddings in [-1, 1]. Pure-python helper for tests."""
    import math

    if not a or not b or len(a) != len(b):
        raise ValueError("embeddings must be non-empty and same length")
    dot = sum(x * y for x, y in zip(a, b))
    na = math.sqrt(sum(x * x for x in a))
    nb = math.sqrt(sum(y * y for y in b))
    if na <= 0.0 or nb <= 0.0:
        raise ValueError("zero-norm embedding")
    return max(-1.0, min(1.0, dot / (na * nb)))
