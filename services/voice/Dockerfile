# TOLIK Voice Service — Dockerfile
# Build context = repository root:
#   docker build -f services/voice/Dockerfile -t tolik-voice .

# -- Build stage -------------------------------------------------------------
FROM python:3.12-slim AS build
WORKDIR /src

# System dependencies for faster-whisper, edge-tts, pyotp, qrcode, and kerberos (for MFA)
# apt over HTTPS: the network blocks plain HTTP/80 to deb.debian.org (observed 04.10).
RUN sed -i 's|http://deb.debian.org|https://deb.debian.org|g' /etc/apt/sources.list.d/debian.sources /etc/apt/sources.list 2>/dev/null || true \
    && apt-get update && apt-get install -y --no-install-recommends \
    build-essential \
    cmake \
    libsndfile1 \
    ffmpeg \
    libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*

# Python dependencies
COPY services/voice/requirements.txt /src/requirements.txt
RUN pip install --no-cache-dir -r /src/requirements.txt

# -- Runtime stage ------------------------------------------------------------
FROM python:3.12-slim AS runtime
WORKDIR /app

# Set HOME to a writable directory for huggingface_hub cache
ENV HOME=/app \
    PYTHONUNBUFFERED=1 \
    PYTHONDONTWRITEBYTECODE=1 \
    TOLIK_VOICE_PORT=9660 \
    TOLIK_STT_ENGINE=faster-whisper \
    TOLIK_WHISPER_MODEL=base \
    TOLIK_STT_DEVICE=cpu

# Runtime dependencies (including espeak for offline TTS fallback on Linux)
# apt over HTTPS: the network blocks plain HTTP/80 to deb.debian.org (observed 04.10).
RUN sed -i 's|http://deb.debian.org|https://deb.debian.org|g' /etc/apt/sources.list.d/debian.sources /etc/apt/sources.list 2>/dev/null || true \
    && apt-get update && apt-get install -y --no-install-recommends \
    libsndfile1 \
    ffmpeg \
    libgomp1 \
    libgssapi-krb5-2 \
    espeak \
    && rm -rf /var/lib/apt/lists/*

# Copy Python packages from build stage
COPY --from=build /usr/local/lib/python3.12/site-packages /usr/local/lib/python3.12/site-packages
COPY --from=build /usr/local/bin /usr/local/bin

# Copy application code
COPY services/voice/server.py /app/server.py
COPY services/voice/dsp_cephalon.py /app/dsp_cephalon.py
COPY services/voice/speaker_verify.py /app/speaker_verify.py
COPY services/voice/requirements.txt /app/requirements.txt
# CampPlus speaker embedding (local personal use only, ~27 MB)
COPY CosyVoice3/pretrained_models/Fun-CosyVoice3-0.5B/campplus.onnx /app/models/campplus.onnx

# Create non-root user
RUN useradd -r -u 1000 -s /bin/bash tolik
RUN chown -R tolik:tolik /app
USER tolik

ENV PYTHONUNBUFFERED=1 \
    PYTHONDONTWRITEBYTECODE=1 \
    TOLIK_VOICE_PORT=9660 \
    TOLIK_STT_ENGINE=faster-whisper \
    TOLIK_WHISPER_MODEL=base \
    TOLIK_STT_DEVICE=cpu

EXPOSE 9660

ENTRYPOINT ["python", "server.py"]

# -- Health check ------------------------------------------------------------
HEALTHCHECK --interval=30s --timeout=10s --start-period=10s --retries=3 \
    CMD python -c "import urllib.request; urllib.request.urlopen('http://127.0.0.1:9660/health', timeout=5)" || exit 1