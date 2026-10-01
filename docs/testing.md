# TOLIK Testing

## How to run

```powershell
dotnet test TOLIK.sln
```

Needs: .NET 10 SDK, local Postgres (integration tests create `tolik_test_*`
databases), Ollama NOT required (model calls are stubbed/absent in tests).

## Coverage map (31 files, 154 tests, green — 146 on 30.09 → 154 now, +4 MemoryOrigin +2 forbidden-surface +2 strict-envelope tests, verified green)

- Authorization/policy/escalation/transport, identity/challenge/evidence,
  contracts, file tools, path isolation.
- Memory: stores, context builder (bounds, rules, persona, speaker), personal
  context, learning/feedback.
- Planner: bounds, execution guard, LLM decomposition (stubbed completion),
  intent router (incl. Cyrillic cases).
- Models: routes, fallback, think/ctx flags, provider failure normalization.
- Voice/TTS providers, profiles, services; perception (stub-level).
- Proactive (scheduler + watchdog decisions), speaker cosine/threshold,
  security logging/redaction, Postgres model shape, home-deployment.
- Integration: bootstrap/enroll, agent loop, multi-step execute, feedback loop,
  memory delete, audit read, enroll/verify speaker (mocked service).

## Gaps (lifecycle tests to add, per master directive §23)

- Development can reset memory; development can use test identity.
- Genesis requires owner auth + MFA; cannot run twice; cannot run with dev
  credentials; creates identity + binding + audit event; interrupted Genesis
  rolls back.
- Lifetime rejects reset/owner-replacement/rerun; preserves audit + memory.
- Negative: unauthorized user, wrong MFA, expired confirmation, dev token in
  lifetime, DB/app restart mid-flow.
- Missing load/chaos/UI tests; whisper/model paths untested (stubs only).
