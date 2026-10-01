# Speaker calibration probe: cosine of enrolled profile vs other voices.
# Usage: python scripts/speaker_calibrate.py
import glob
import json
import os
import subprocess

BASE = r"C:\DEV\TOLIK_QWEN"
VOICE = "http://127.0.0.1:9660/voice/speaker/embed"


def embed(path):
    out = subprocess.run(
        ["curl.exe", "-s", "-S", "--max-time", "60", "-X", "POST",
         "-F", "file=@" + path, VOICE],
        capture_output=True)
    try:
        data = json.loads(out.stdout.decode("utf-8"))
    except Exception:
        return None
    return data.get("embedding")


def cos(x, y):
    return sum(p * q for p, q in zip(x, y))


ref = embed(BASE + r"\artifacts\cosyvoice-dataset\clips\ref0002.wav")
print("enrolled ref0002 ok, dim=%d" % len(ref))

probes = (
    sorted(glob.glob(BASE + r"\artifacts\cosyvoice-dataset\clips\ref*.wav"))[:6]
    + sorted(glob.glob(BASE + r"\artifacts\cosyvoice-dataset\wav\suit_male\*.wav"),
             key=os.path.getsize, reverse=True)[:10]
)
for path in probes:
    embedding = embed(path)
    score = "SHORT/FAIL" if embedding is None else "%.3f" % cos(ref, embedding)
    print("%6.0fKB %-34s -> %s" % (os.path.getsize(path) / 1024, os.path.basename(path)[:34], score))
