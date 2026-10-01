# TOLIK Security

## Threat Model

TOLIK handles sensitive personal data:
- Private conversations
- Personal information
- Credentials and tokens
- Home environment data
- Computer access

## Security Layers

### 1. Identity
- Device identity verification
- Session-based authentication
- Bootstrap code for initial setup
- Challenge-response for revocation
- Speaker verification (CampPlus embedding, threshold 0.45): `POST /voice/speaker/enroll`
  stores a Private voice profile, `POST /voice/speaker/verify` returns confidence.
  Voice confidence alone never authorizes — enforcement (low-confidence
  restrictions in voice turns) is the next step, not yet wired.

### 2. Authorization
- Per-tool capability system
- READ_ONLY / LOW_RISK / MODIFY / DESTRUCTIVE / CRITICAL
- Context-aware authorization
- Audit trail for all decisions

### 3. Tool Security
- Explicit capability list per tool
- Path isolation (TOLIK_ALLOWED_ROOTS)
- Never unrestricted shell access
- Destructive operations require confirmation
- Agent disconnect is owner-authenticated; Core atomically removes the connection and command queue, cancels pending command completions, and audits allow/deny decisions. Reconnect creates a fresh queue; unknown or foreign agents return 404.

### 4. Sensitive Data
- Secrets in .env only (never committed)
- API keys redacted in logs
- Tokens not returned to clients
- Credential files excluded from version control
- Authorization headers never logged (401 diagnostics record only path + header presence)

### 6. Local dev convenience (single-user machines only)
- `TOLIK_DEV_AUTO_TOKEN=true` allows the same-machine WebUI to mint an owner
  session via `GET /auth/local-token` (explicit opt-in, default off).
- Endpoint answers only loopback/RFC1918 callers and 404s otherwise
  (indistinguishable from "no such endpoint" for outsiders).
- Core logs a startup warning while enabled. Never enable on shared/VPS hosts.

### 5. Audit
- All important actions logged
- Structured audit events in PostgreSQL
- Actor, tool, arguments, result, timestamp
- Secrets never logged
- `audit_events` is append-only at both EF boundary and PostgreSQL trigger boundary
- `GET /audit` (authenticated, paginated) exposes the trail: Owner sees all,
  others only their own user-scoped events; auth endpoints (bootstrap, enroll,
  revoke) are audited ALLOW/DENY without logging codes, keys or tokens

## .gitignore Key Rules
- .env files
- API keys, passwords, tokens
- Credential files
- Local development data

## Bootstrap Flow
1. First run → bootstrap challenge
2. User provides bootstrap code
3. Device registered with identity key
4. Session credentials issued
5. Future: revocation capability
