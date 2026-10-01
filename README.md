# TOLIK — Personal AI Agent System

> **TOLIK is not a chatbot.** It is a long-lived personal AI system with reasoning, memory, identity, voice, computer perception, and autonomous agents.

## Architecture

`\nClient (CLI / Web / Android) → TOLIK Core (ASP.NET) → Agent → Execution
`

### Key components
- **TOLIK Core** — central brain (reasoning, planning, memory, identity, personality)
- **Windows Agent** — execution on Windows (filesystem, processes, tools)
- **Model Router** — routes tasks to appropriate LLM (cloud via OpenAI-compatible API, or local via Ollama)
- **CosyVoice3** — TTS synthesis with voice cloning
- **PostgreSQL** — persistent storage for memory, tasks, identity, audit

### LLM Modes
- **Online** — OpenAI-compatible provider (https://dindindon.ru/v1/)
- **Offline** — Ollama local model (http://localhost:11434)
- Switch via TOLIK_OFFLINE_MODE=true in .env

## Quick Start

### Prerequisites
- **.NET 10 SDK** — installed
- **Docker** — installed
- **Ollama** — installed (qwen3.6:35b-coding recommended)
- **PostgreSQL** — via docker-compose

### Run
`powershell
docker-compose up -d
cp .env.example .env  # edit with your values
dotnet run --project src/TOLIK.Core.Host
`

### APIs
| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | /health | Health check |
| POST | /auth/bootstrap | Bootstrap identity |
| POST | /auth/session | Create session |
| POST | /chat | Send message |
| POST | /voice | Voice input |
| GET | /status/modules | System status |
| GET | /agents | Connected agents |

## Voice
- Voice reference data: oice_reference/ (~7000 mp2 files)
- Dataset preparation: scripts/prepare-cosyvoice-dataset.ps1
- Transcription: scripts/auto-transcribe.ps1
- CosyVoice3 repo: CosyVoice3/

## Memory
- Conversations, tasks, events, semantic memory, episodic memory
- Family lore, humor, preferences
- PostgreSQL-backed, persistent

## Security
- Identity verification (bootstrap code)
- Authorization per tool capability
- Sensitive data redaction
- Audit logging

## Repository Structure
`
src/
  TOLIK.Contracts    — Shared contracts (DTOs, interfaces)
  TOLIK.Core         — Core logic (planner, memory, TTS)
  TOLIK.Core.Host    — ASP.NET API host
  TOLIK.Infrastructure — Persistence, identity, auth
  TOLIK.Agent         — Agent protocol
clients/
  TOLIK.Cli          — CLI client
tests/
  TOLIK.Foundation.Tests
cosyvoice-dataset/   — Prepared voice dataset
voice_reference/     — Source voice recordings
scripts/             — Build, prepare, test scripts
docs/                — Architecture, memory, security docs
