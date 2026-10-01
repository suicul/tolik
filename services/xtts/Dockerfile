# TOLIK XTTS Service — Dockerfile (GPU sidecar, opt-in via compose profile "gpu").
# Build context = repository root:
#   docker build -f services/xtts/Dockerfile -t tolik-xtts .
FROM pytorch/pytorch:2.5.1-cuda12.1-cudnn9-runtime AS runtime
WORKDIR /app

ENV PYTHONUNBUFFERED=1 \
    PYTHONDONTWRITEBYTECODE=1 \
    HOME=/data \
    COQUI_TOS_AGREED=1 \
    TOLIK_XTTS_PORT=9670 \
    TOLIK_XTTS_SPEAKER=/data/prompt.wav \
    HF_HOME=/data/hf-cache \
    NUMBA_CACHE_DIR=/tmp/numba-cache

RUN apt-get update && apt-get install -y --no-install-recommends \
    ffmpeg \
    libsndfile1 \
    && rm -rf /var/lib/apt/lists/*

COPY services/xtts/requirements.txt /app/requirements.txt
RUN pip install --no-cache-dir -r /app/requirements.txt

COPY services/xtts/server.py /app/server.py

RUN useradd -r -u 1000 -s /bin/bash tolik
RUN mkdir -p /data /data/hf-cache && chown -R tolik:tolik /app /data
USER tolik

EXPOSE 9670

ENTRYPOINT ["python", "server.py"]

HEALTHCHECK --interval=30s --timeout=10s --start-period=120s --retries=3 \
    CMD python -c "import urllib.request; urllib.request.urlopen('http://127.0.0.1:9670/health', timeout=5)" || exit 1
