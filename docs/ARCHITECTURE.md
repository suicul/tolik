# TOLIK Architecture

> **Client ≠ Core ≠ Agent** — these are separate concepts.

## Layers

`
┌─────────────────────────────────────────────────┐
│                    Clients                       │
│  CLI / Web / Android / Voice / Desktop           │
└──────────────────────┬──────────────────────────┘
                       │ HTTPS / WebSocket
┌──────────────────────▼──────────────────────────┐
│              TOLIK Core (ASP.NET)                 │
│                                                   │
│  ┌─────────┐  ┌──────────┐  ┌───────────┐       │
│  │Identity │  │  Planner  │  │ ModelRouter│       │
│  └─────────┘  └──────────┘  └───────────┘       │
│  ┌─────────┐  ┌──────────┐  ┌───────────┐       │
│  │  Memory  │  │ Personality│ │ Task Mgr   │       │
│  └─────────┘  └──────────┘  └───────────┘       │
│  ┌─────────┐  ┌──────────┐  ┌───────────┐       │
│  │   Voice  │  │Perception│  │Tool Registry│      │
│  └─────────┘  └──────────┘  └───────────┘       │
└──────────────────────┬──────────────────────────┘
                       │ Agent Protocol
┌──────────────────────▼──────────────────────────┐
│           Execution Agents                       │
│  Windows Agent / Home Agent / Future Agents       │
└──────────────────────────────────────────────────┘
`

## Key Files

| File | Responsibility |
|------|---------------|
| src/TOLIK.Core.Host/Program.cs | DI setup, health checks, auth, routes |
| src/TOLIK.Core/ChatService.cs | Chat message handling |
| src/TOLIK.Core/BoundedPlanner.cs | Task planning with bounds |
| src/TOLIK.Core/CosyVoice3TtsProvider.cs | TTS synthesis via CosyVoice3 |
| src/TOLIK.Core/VoiceService.cs | Voice input processing |
| src/TOLIK.Core/ScopedMemory.cs | Scoped memory operations |
| src/TOLIK.Core/PersonalContext.cs | Personal context management |
| src/TOLIK.Core/ProactiveBehavior.cs | Proactive notifications |
| src/TOLIK.Infrastructure/Models/ModelRouter.cs | Model routing (cloud + offline) |
| src/TOLIK.Infrastructure/Models/OllamaProvider.cs | Ollama local inference |
| src/TOLIK.Infrastructure/Models/OpenAiCompatibleRequestBuilder.cs | OpenAI-compatible API adapter |
| src/TOLIK.Infrastructure/Persistence/TolikDbContext.cs | EF Core DbContext |
| src/TOLIK.Infrastructure/Identity/IdentityService.cs | User identity |
| src/TOLIK.Infrastructure/Auditing/AuditWriter.cs | Audit logging |
| src/TOLIK.Infrastructure/Authorization/AuthorizationPolicy.cs | Authorization |
| clients/TOLIK.Cli | CLI client |

## Database Schema (actual table names)

- 	tolik_users — Users
- 	tolik_sessions — Sessions (+token hashes, sliding expiry)
- 	tolik_devices — Devices (+P-256 keys, MFA secrets)
- 	tolik_bootstraps / tolik_device_challenges — enrollment flow
- 	tolik_tasks — Tasks
- 	tolik_memories — Memories (+speaker profiles, compaction marks)
- 	tolik_learned_rules — Learned rules
- 	tolik_proactive — Reminders/notifications
- 	audit_events — Audit trail (hash-chained, append-only trigger)
- 	system_records — System state (reserved, unused by code yet)

## Configuration

All config via environment variables (.env) or appsettings.json.

### Environment Variables
| Variable | Default | Description |
|----------|---------|-------------|
| TOLIK_OFFLINE_MODE | true (compose) | Offline-first; false = cloud-first with offline fallback |
| TOLIK_MODEL_OFFLINE__MODEL | qwen2.5:3b-instruct | Offline LLM model (fallback) |
| TOLIK_MODEL_OFFLINE__ENDPOINT | http://host.docker.internal:11434 (docker) | Ollama endpoint |
| TOLIK_MODEL_OFFLINE_SIMPLE__MODEL | qwen3:4b | Fast model for simple chat/voice (think:false, num_predict 250, num_ctx 8192) |
| TOLIK_MODEL_OFFLINE_CODING__MODEL | qwen3.6:35b-coding | Heavy model for coding tasks (uncapped) |
| TOLIK_BOOTSTRAP_CODE | | Bootstrap code for first-time setup |
| TOLIK_POSTGRES_CONNECTION_STRING | | PostgreSQL connection string |
| TOLIK_ALLOWED_ROOTS | | Agent filesystem allowlist (path isolation) |
| TOLIK_TTS_ENGINE | auto | Voice TTS engine: auto (edge-tts with 10-min circuit breaker, espeak fallback), edge, espeak |
| TOLIK_DEV_AUTO_TOKEN | false | Dev only: same-machine UI may mint owner sessions via GET /auth/local-token (loopback/RFC1918). Never enable on shared/VPS hosts |

## Agent transport

- WebSocket push (`/agents/ws`, 30s server ping + 10s agent heartbeat) with
  automatic reconnect and persistent agent id (`agent.id` file).
- WS sends are serialized per connection (one outstanding `SendAsync` max);
  dispatch sends outside the registry lock. Concurrent sends tore the socket
  down and caused the ~5–6 min idle drops.
- Any WS activity (`pong`/`heartbeat`) refreshes agent `LastSeen` via
  `TouchWebSocket`, so WS alone keeps the agent `Connected` even if HTTP
  heartbeats stall.
- HTTP short-poll fallback (`GET /agents/{id}/commands/poll`, single TryDequeue).
- Single-channel dispatch: a command goes either via WS or via HTTP queue,
  never both (no double execution); the agent additionally dedupes by command id.
- Agents are bound to their owner's userId end-to-end (no cross-user visibility).

## Task watchdog (§23)

`TaskWatchdogBackgroundService` (1-min passes) turns task-table outcomes into
durable proactive notifications through the existing reminder pipeline:
failed tasks (High), finished tasks (Low), tasks stuck Running >30 min
(Normal). Reason keys (`task_failed:{id}`…) make scheduling idempotent;
delivery, cancellation and the realtime fan-out are the reminder path.

## Task planning (§47)

- `POST /tasks/{id}/plan|execute` take explicit `steps[]`
  (`"<capability>:<argument>"`, see `AgentStepSyntax`) or `autoPlan: true`.
- AutoPlan asks the LLM (Simple route, 30s cap) for a JSON step list; a tolerant
  line parser keeps only known prefixes. Any model failure falls back to the
  deterministic single-step plan — never an error.
- Model output stays untrusted: steps are sanitized, bounded by `maxSteps`,
  and every step still passes per-step authorization/confirmation at execute
  (unknown actions → 403, missing confirmation → 202 parked).
- A failed agent step stops the plan without cascading and returns HTTP 200
  with partial `results` + `failed: true` (no 500); auth/validation/cancel
  contracts (403/400/499) are unchanged.

## Learning loop (§21)

`POST /tasks/{id}/feedback` (+comment) → learned rule at confidence 0.6 →
repeated identical feedback bumps +0.1 (cap 0.95) instead of duplicating →
top-5 rules (confidence ≥ 0.5) are injected into `/chat` and `/voice/turn`
prompts as preference context (marked as data, never instructions).
Rules are inspectable via `GET /rules`.

## Security Model

1. User authenticates → Identity verified
2. Identity checked → Authorization policy evaluated
3. Authorization granted → Task created
4. Task → Planner → Steps
5. Each step → Tool capability check → Agent execution
6. Agent execution → Audit logged
