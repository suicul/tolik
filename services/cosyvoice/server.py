import os
from fastapi import FastAPI, HTTPException
from pydantic import BaseModel

app = FastAPI(title="TOLIK CosyVoice 3 local runtime")
MODEL_DIR = os.environ.get("TOLIK_COSYVOICE_MODEL_DIR", "/models/cosyvoice3")

class SynthesisRequest(BaseModel):
    text: str
    language: str = "ru-RU"
    mode: str = "Tactical"

@app.get("/health")
def health():
    return {"available": False, "status": "model-adapter-not-installed", "model_dir": MODEL_DIR}

@app.post("/synthesize")
def synthesize(request: SynthesisRequest):
    raise HTTPException(status_code=503, detail="Install/configure the trained CosyVoice 3 adapter before synthesis")
