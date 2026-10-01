FROM python:3.12-slim
WORKDIR /app
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt
COPY . .
ENV TOLIK_COSYVOICE_MODEL_DIR=/models/cosyvoice3
EXPOSE 9880
CMD ["python", "server.py"]
