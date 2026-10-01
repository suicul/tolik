# TOLIK — VPS migration runbook (§55)

Target: Core (+Postgres, +Voice) on a VPS; Windows PC becomes a remote agent.
The Core already avoids machine-local dependencies: no Windows filesystem
(`TOLIK_ALLOWED_ROOTS` is agent-side), no local GPU requirement, all
endpoints/credentials via environment.

## Preconditions

- `.env` filled from `.env.example` (never committed).
- `TOLIK_DEV_AUTO_TOKEN=false` (default; the launcher sets it `true` for
  same-machine dev only — never on a shared/VPS host).
- `TOLIK_BOOTSTRAP_CODE` set to a fresh secret for first enrollment.
- Models: `host.docker.internal` Ollama does not exist on a VPS. Either run
  Ollama on the VPS and point `TOLIK_MODEL_OFFLINE__ENDPOINT` at it, or use
  cloud routes (`TOLIK_MODELS_SIMPLE/REASONING/CODING/VISION__*`) with
  `TOLIK_OFFLINE_MODE=false`.

## Start

```sh
docker compose --profile vps up -d --build
docker compose --profile vps ps   # postgres + voice healthy, core healthy
```

Core `healthcheck` hits `/health/ready` (postgres reachable). Startup applies
EF migrations automatically; `start_period: 90s` covers first-boot migrate.

## Expose

- Compose binds loopback only (`127.0.0.1`). Remote agents/clients go through
  a reverse proxy with TLS, or Tailscale. Network reachability is NOT
  authorization: every caller still needs device enrollment + session +
  per-tool permission checks.
- Home agent: `agent --core-url https://<vps> --token <session-token>`
  over the secure channel; heartbeat + WS auto-reconnect apply unchanged.

## Operator duties (not automated)

- Back up the `tolik_postgres_data` volume (memory, audit, tasks).
- Rotate `TOLIK_BOOTSTRAP_CODE` after enrollment; revoke lost devices
  (revocation API; stolen hosts become untrusted agents per anti-theft model).
- Watch audit/security events; independent owner notifications are future work.
- Voice: STT (faster-whisper CPU) is heavy for small VPS; TTS falls back to
  offline espeak when Microsoft endpoints are unreachable (10-min breaker).

## Not yet

- Managed TLS inside compose (operator terminates TLS outside).
- Automatic backup/restore tooling.
- Independent (out-of-host) owner notification channel.
