# TOLIK UI prototype

Two modes, one Core:
- `Chat` — message feed, task cards (capability + declared/effective risk + decision), confirmation box, audit.
- `Cephalon` — canvas eye (idle/thinking/speaking/denied/locked), subtitle typewriter, same input.

Runs `src/tolik/core.js` in-browser with `persistentIdentity:false`.
Workarounds (prototype only, not Core fixes):
- re-pin `win-01` with raw `install.kid` (Core pins `agent:<kid>`, transport signs raw kid).
- `confirmationFor` accepts `bindingHash` and backfills `boundTo` (Core records `bindingHash`, policy checks `boundTo`).
