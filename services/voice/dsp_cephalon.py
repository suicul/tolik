"""Cephalon voice character DSP (AGENTS.md §14: original Russian ship-cephalon feel).

Post-processing chain applied to ANY TTS engine output, so the character does
not depend on the synthesis backend:
  1. pitch down (~0.92x via resampling — deeper register, slightly measured pace)
  2. ring modulation (~38 Hz — metallic synthetic texture)
  3. short comb reverb (ship-compartment tail)
  4. mild bit-crush (digital grit)
  5. optional random glitch stutter (Ordis-style speech faults, §17-like fun)

numpy-only; mp3/wav decode via the ffmpeg already present in the image.
All parameters are explicit; failures must never break synthesis — callers
fall back to the unprocessed audio on any exception.
"""

import io
import subprocess
import tempfile
import wave

import numpy as np

TARGET_SR = 24000


def decode_to_mono(data: bytes, target_sr: int = TARGET_SR) -> tuple[np.ndarray, int]:
    """Decode mp3/wav bytes to mono float32 in [-1, 1]."""
    with tempfile.NamedTemporaryFile(suffix=".in", delete=False) as src:
        src.write(data)
        src_path = src.name
    out_path = src_path + ".wav"
    try:
        result = subprocess.run(
            ["ffmpeg", "-hide_banner", "-loglevel", "error", "-y",
             "-i", src_path, "-ac", "1", "-ar", str(target_sr),
             "-sample_fmt", "s16", out_path],
            capture_output=True, timeout=60)
        if result.returncode != 0 or not _exists(out_path):
            raise RuntimeError(f"ffmpeg decode failed: {result.stderr[-200:]!r}")
        with wave.open(out_path, "rb") as wav:
            frames = wav.readframes(wav.getnframes())
            width = wav.getsampwidth()
        if width == 2:
            samples = np.frombuffer(frames, dtype=np.int16).astype(np.float32) / 32768.0
        elif width == 1:
            samples = (np.frombuffer(frames, dtype=np.uint8).astype(np.float32) - 128.0) / 128.0
        else:
            raise RuntimeError(f"unsupported sample width: {width}")
        return samples, target_sr
    finally:
        _silent_unlink(src_path)
        _silent_unlink(out_path)


def encode_wav(samples: np.ndarray, sample_rate: int = TARGET_SR) -> bytes:
    """Encode mono float32 in [-1, 1] to 16-bit PCM wav bytes."""
    clipped = np.clip(samples, -1.0, 1.0)
    pcm = (clipped * 32767.0).astype(np.int16)
    buffer = io.BytesIO()
    with wave.open(buffer, "wb") as wav:
        wav.setnchannels(1)
        wav.setsampwidth(2)
        wav.setframerate(sample_rate)
        wav.writeframes(pcm.tobytes())
    return buffer.getvalue()


def pitch_down(samples: np.ndarray, ratio: float = 0.92) -> np.ndarray:
    """Deepen the register by resampling (ratio < 1: lower + slightly slower)."""
    if ratio <= 0 or ratio >= 1.5 or len(samples) < 16:
        return samples
    new_len = max(16, int(len(samples) / ratio))
    return np.interp(
        np.linspace(0.0, len(samples) - 1, new_len),
        np.arange(len(samples), dtype=np.float64),
        samples).astype(np.float32)


def ring_modulate(samples: np.ndarray, sample_rate: int, freq: float = 38.0, depth: float = 0.25) -> np.ndarray:
    """Metallic synthetic texture: amplitude wobble at a low modulation rate."""
    t = np.arange(len(samples), dtype=np.float64) / sample_rate
    return (samples * (1.0 + depth * np.sin(2.0 * np.pi * freq * t))).astype(np.float32)


def comb_reverb(samples: np.ndarray, sample_rate: int,
                delays_ms: tuple = (23.0, 31.0), feedback: float = 0.22, wet: float = 0.15) -> np.ndarray:
    """Cheap ship-compartment tail: parallel feedback combs, mostly dry."""
    out = samples.copy()
    for delay_ms in delays_ms:
        delay = max(1, int(sample_rate * delay_ms / 1000.0))
        tail = np.zeros_like(out)
        for n in range(delay, len(out)):
            tail[n] = out[n] + feedback * tail[n - delay]
        out = out + wet * (tail - out)
    return out.astype(np.float32)


def bit_crush(samples: np.ndarray, bits: int = 13) -> np.ndarray:
    """Mild digital grit."""
    levels = float(2 ** max(4, min(bits, 15)))
    return (np.round(samples * levels) / levels).astype(np.float32)


def glitch_stutter(samples: np.ndarray, sample_rate: int, rng: np.random.Generator,
                   repeats: int = 2) -> np.ndarray:
    """Repeat one 30–60 ms slice a few times (Ordis speech fault)."""
    if len(samples) < int(sample_rate * 0.6):
        return samples
    start = int(rng.integers(int(len(samples) * 0.1), int(len(samples) * 0.85)))
    width = int(rng.integers(int(sample_rate * 0.03), int(sample_rate * 0.06)))
    width = min(width, len(samples) - start)
    if width <= 0:
        return samples
    fault = np.tile(samples[start:start + width], repeats)
    return np.concatenate([samples[:start], fault, samples[start:]]).astype(np.float32)


def cephalon(samples: np.ndarray, sample_rate: int, glitch_prob: float = 0.18,
             seed: int | None = None) -> np.ndarray:
    """Full character chain. Returns processed mono float32."""
    out = pitch_down(samples)
    out = ring_modulate(out, sample_rate)
    out = comb_reverb(out, sample_rate)
    out = bit_crush(out)
    if glitch_prob > 0.0:
        rng = np.random.default_rng(seed)
        if rng.random() < glitch_prob:
            out = glitch_stutter(out, sample_rate, rng)
    peak = float(np.max(np.abs(out))) if len(out) else 0.0
    if peak > 0.89:
        out = (out * (0.89 / peak)).astype(np.float32)
    return out


def process_request(audio: bytes, glitch_prob: float = 0.18) -> bytes:
    """Decode → cephalon chain → wav bytes. Raises on decode failure."""
    samples, rate = decode_to_mono(audio)
    return encode_wav(cephalon(samples, rate, glitch_prob))


def _exists(path: str) -> bool:
    try:
        with open(path, "rb"):
            return True
    except OSError:
        return False


def _silent_unlink(path: str) -> None:
    import os
    try:
        os.unlink(path)
    except OSError:
        pass
