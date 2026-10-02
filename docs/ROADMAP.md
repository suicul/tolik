# TOLIK Master Plan (30.09.2026)

Living plan: status + next actions per track. VPS migration is distant-future
by owner decision — not on the active list.

## Model policy (owner-approved 30.09.2026, cloud keys live since)

- Chatter/voice (Simple): **qwen3:4b local**, `think:false`, `num_predict:250`, `num_ctx:8192`.
- Reasoning/Coding: **cloud gpt-6.1-sol** (tooken.club, key in `.env`), offline fallback.
- Measured: qwen3:4b @100% GPU = ~100 tok/s; default 256k ctx drops it to 77% CPU / ~18 tok/s.
  `OLLAMA_NUM_CTX=8192` is set in user env AND forced per-request by the router.
- Ask-policy for coding (cloud vs local + remembered preference): DONE 02.10.2026 —
  `ModelPreferenceStore` (in-memory per-user; restart clears, ask re-presented) +
  `ResolveWithFallback(kind, preference)` override (Local → offline-first coding,
  Cloud → cloud-first default, Simple unaffected) + `POST/GET/DELETE /models/preference`
  (authenticated, per user, invalid mode → 400); wired into /chat + /voice/turn.

## Track 1 — Assistant core ✅ mostly done
Done: API/auth/memory/tasks/planner/AutoPlan/partial-failures/audit+read/
agent transport/Windows agent/CLI/Linux-shim/proactive (reminders+watchdog)/
MFA/speaker enroll+verify+guest mode/persona/242 tests/green (146 on 30.09 → 242 now, +4 MemoryOrigin +2 forbidden-surface +2 strict-envelope +18 roles/ceilings +9 lease +10 locked-state +8 model-preference +10 transport-frame +15 family/enrollment +8 sanitizer +4 key-exchange +6 genesis-ceremony tests, verified green).
Genesis ceremony subsystem DONE 03.10.2026 (Phase 6): preflight + instance identity
table + owner binding + atomicity + rerun impossibility.
PRE-GENESIS hardening pass DONE 03.10.2026 (live): core aligned + миграция
20261002174934 применена (tolik_genesis_identity пуста, ceremony не запускалась),
memory separation gate: 0 development-строк на live, smoke PASS (origin round-trip,
genesis gates fail-closed: 400 preflight_failed без MFA/backup), pre-genesis backup
validated (tolik-backup-pre-genesis.dump, ~2.05 MB, SHA256 sidecar; файл во временной
папке — переместить в постоянное хранилище перед церемонией).
Осталось перед READY_FOR_GENESIS (внешние действия владельца на момент церемонии):
TOLIK_DEV_AUTO_TOKEN off, backup/restore attestations, MFA код, запуск ceremony.
Next: enroll wife's voice + family role scoping (§18, wife = scoped scenarios).
Family scoping DONE 02.10.2026: roles TrustedGuest/Family + ceilings + minimum-role
matrix (Codex/OpenCode/RunProcess→Owner) + owner-issued enrollment tokens
(POST /auth/enrollment-token → POST /auth/enroll, single-use, TTL 10 min, audited).
Next: wife's actual enroll (external action — mint a token, run enroll on her device).

## Track 2 — Voice ✅ working, quality loop open
Done: STT (faster-whisper, `language` param), XTTS-v2 GPU sidecar approved by ear
and enabled by default, DSP chain (off), whisper dataset pipeline (985 clips,
medium/en transcripts, review pending).
Next: review.tsv curation → owner's CosyVoice fine-tune (owner's track);
normalization dictionary (Цетус/Гринир seed list ready); game-mode mute.

## Track 3 — Native Windows UI (WPF, approved) 🔲 not started
Cephalon overlay: transparent always-on-top, idle/listening/thinking/speaking/
working/denied states, direct mic (no browser), mic-always-on WITH indicator
(approved), game mode silent. Web UI stays as fallback. Voice-ID enroll wizard exists in Web UI (3 phrases +
verify); port it. Needs: wake-word (openWakeWord) integration,
challenge-response for sensitive voice actions.

## Track 4 — Memory++ (biggest quality lever after voice)
1. pgvector + embeddings (associative recall; Postgres ready).
2. Auto event extraction from conversations (promises/facts without «запомни»).
3. Priorities/decay/contradiction handling; expires_at schema gap.
4. Cross-user knowledge map.

## Track 5 — Autonomy (supervised self-coding)
Have: run_codex/run_opencode steps, build/test gates, confirmations.
Missing: replan-on-failure loop, own-diff review, rollback, sandbox scopes
(never self-edit authorization). Order: propose-diff → build+test → ask;
then autonomy inside allowlisted dirs.

## Track 6 — Perception & clients (later)
Vision/screen understanding (zero), Android client (zero, Kotlin),
desktop tray (part of Track 3), expires_at, owner notifications channel.

## Standing pending items (no owner needed to start)
- Cloud API keys → enable Reasoning/Coding/Vision cloud routes + ask-policy.
- review.tsv curation (owner's ears).
- Wife voice enrollment + role check.
- gpt-oss:20b benchmark (13 GB pull) — only if qwen3:4b disappoints.
