# Luxify Feature Analysis (PRE-GENESIS extraction)

Source: official docs `https://luxify-assistant.com/en/docs` (fetched 01.10.2026).
No Luxify code, binaries, or assets were copied, inspected, or used.
Labels below: **FACT** = stated in the docs; **INFERRED** = reasonable guess, not relied upon.

Rule applied throughout: TOLIK architecture is canonical. Only primitives that
strengthen it without new subsystems qualify for P0. Everything else → P1 backlog
or `docs/post-genesis-roadmap.md`.

## Feature matrix

| Luxify feature | What it does (FACT) | Useful to TOLIK | TOLIK analog | Decision | Priority |
|---|---|---|---|---|---|
| Command tree (collection→folder→command) | User-defined commands, enable/disable toggles, search, clone, drag-reorder | Organized user commands | Partial: hardcoded `IntentRouter` regex intents, no user-defined commands | DEFER | P1 |
| Activation + optional filler phrases | Required phrase + skippable fillers; warns on phrase conflicts | Natural phrasing | Partial: regex intents, no filler model, no conflict check | DEFER | P1 |
| Import/export commands as JSON, dangerous content flagged + auto-confirm | Share/move commands; scripts/shutdown/file-write trigger confirm offer | Knowledge portability | Partial: DB backup covers disaster case, no per-item export | DEFER | P1 |
| Multi-action ordered steps + pauses | Any number of actions per command, reorder, wait steps | Sequencing | **ALREADY_EXISTS**: `BoundedPlan` multi-step + `PlanExecutionGuard` (pauses pointless: TOLIK has no GUI keypresses) | ALREADY_EXISTS | — |
| 10 action groups (launch/keyboard/mouse/program/voice/sound/system/browser/text/files) | Broad desktop control incl. mouse/keyboard/screen | — | Partial: 9 capabilities, deliberately NO mouse/keyboard/shell (security boundary). Keyboard/mouse = rejected by TOLIK design | SKIP (+mouse/kbd) | — |
| Command chaining ("A and B") | Linking words join marked commands | One-shot compound requests | **ALREADY_EXISTS**: `AutoPlan` LLM decomposition (different mechanism, same outcome) | ALREADY_EXISTS | — |
| Create with AI (NL → steps, not auto-saved) | Describe command, review suggested steps | Same as above | **ALREADY_EXISTS** | ALREADY_EXISTS | — |
| Record actions (clicks/keys/launches→editable steps) | Demonstrate once, replay always | Scenario capture | Missing. Requires computer-use/vision which TOLIK lacks | DEFER | P2 |
| Command packs (installable collections, preview, update preserves edits) | Ready-made commands per app/game | Fast onboarding | Missing. Needs user-command substrate first | DEFER | P2 |
| Wake word (configurable prefix, prefix/silent modes, follow-up window, "stop" interrupt) | Hands-free trigger + gating | Hands-free | Missing by decision (master directive §13/§30: do not start) | DEFER | P2 |
| Command history (what was heard + what was done) | Single audit-like UX line per command | Genesis verifiability, UX | Partial: task events + `audit_events` + memory exist, but no unified heard+done view | ADAPT | P1 |
| Voice yes/no confirmation (30s timeout) | Spoken approve/deny | Hands-free approvals | Partial: confirmations exist (10-min expiry, Owner-only, MFA-gated Critical) but no voice input path | ADAPT | P1 |
| Avatar presence (circles, reacts to voice, click-through, edge snap) | Visual embodiment | Presence | Partial: web eye states `idle/thinking/speaking/working/denied/locked` driven by the same realtime events | ALREADY_EXISTS (mechanism) | P2 (full avatar) |
| Character voices + per-action voice lines | Persona flavor | Flavor | Partial: XTTS voice + persona prompt; per-action lines absent | DEFER | P2 |
| Structured action result states | Clear lifecycle per action | Unambiguous outcomes | **ALREADY_EXISTS**: `Pending/Running/Paused/Completed/Failed/Cancelled` + parked-202 + failed-with-partials + audit decisions (explicit `TimedOut` state absent, timeout folds into `Failed` with reason — sufficient) | ALREADY_EXISTS | — |
| Offline-first commands+recognition | Works without internet | Resilience | **ALREADY_EXISTS**: Ollama + local STT + local XTTS path | ALREADY_EXISTS | — |
| Telegram remote control (owner-gated bot, confirm-via-button) | Phone control | Remote ops | Partial: Tailscale + thin Linux client + opencode attach cover remote; no messenger bot | DEFER | P2 |
| Computer binding slots / licensing | 1–3 machines, 30-day unbind | — | Not applicable: TOLIK is personal infra, device revoke already exists | SKIP | — |
| Cloud AI with built-in key / 12 providers | Chat with web/image/file/screenshot | Model choice | Partial: `ModelRouter` (Ollama + OpenAI-compatible cloud, 4 task kinds); no web search, no image input path | DEFER | P2 |

## P0 selection for PRE-GENESIS

**Empty set — deliberately.** Every candidate either already exists in TOLIK
in equivalent form, requires a subsystem Genesis forbids (vision, wake word,
messenger), or is UX convenience rather than birth-readiness. Inventing P0 work
here would violate Rule №1. The two ADAPT/P1 items above (unified heard+done
history view; voice yes/no confirmation input) go to the P1 backlog, not into
the Genesis scope.

## P1 backlog (not Genesis-blocking)

1. Unified command history view (read model over task events + audit + memory;
   heard-text + done-result in one line; no new tables).
2. Voice yes/no confirmation input (speech → approve/deny pending confirmation;
   same binding/expiry/MFA rules, new input path only).

## P2 / post-genesis (see `docs/post-genesis-roadmap.md` when created)

Record actions, command packs + user-defined commands, wake word (+ trigger
interface note), full avatar, Telegram bot, cloud AI extras (web/image),
marketplace-style sharing.

## Security note (binding)

Any adapted primitive must pass the existing chain
`Intent → Command → Authorization → MFA/Confirmation → Action → Tool → Audit → Result`.
TOLIK has no `User → shortcut → Tool` path and must never gain one; P1 items
above reuse `ConfirmationStore` binding/expiry and audit writers.
