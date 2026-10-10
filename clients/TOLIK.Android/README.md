# TOLIK Android client (thin client, Track 16 AN-0..AN-2)

TOLIK Android is a **thin interface to TOLIK Core** (AGENTS.md §36): it
authenticates with a Core-issued session token, talks to the Core HTTP/WS
surface, and contains **no Core logic, no authorization policy, no model
credentials**. The device is an untrusted client (SECURITY & TRUST MODEL §1):
Core 401/403 responses are failures, never local authorization, and network
location is never authorization (§37).

## Status

**Compile-pending**: the Android SDK / Gradle are not installed on the current
dev machine. The code is written carefully against the actual Core surface
(`src/TOLIK.Core.Host/Program.cs`) but has **not been built or run**. Once the
SDK lands:

```bash
cd clients/TOLIK.Android
gradle wrapper --gradle-version 8.7   # generates the wrapper (not committed)
./gradlew :app:assembleDebug
```

## Stack (decisions fixed)

- Kotlin + classic Views (XML layouts, Material 3) — not Jetpack Compose
- minSdk 26, targetSdk 34, compileSdk 34
- Single Activity (`MainActivity`), manual wiring — no DI frameworks (§43/§44)
- Coroutines + OkHttp 4 + Gson (matches the dependency-free contract layer)

## Project structure

```text
clients/TOLIK.Android/
├── README.md                      ← this file (contract notes + structure)
├── .gitignore                     ← Android/Gradle/IDE/signing noise
├── settings.gradle.kts            ← single :app module
├── build.gradle.kts               ← pinned AGP 8.5.2 / Kotlin 2.0.20
├── gradle.properties              ← AndroidX, JVM args
└── app/
    ├── build.gradle.kts           ← module config + deps (OkHttp/Gson/coroutines/Material)
    ├── proguard-rules.pro         ← keeps the Gson model classes for R8
    └── src/main/
        ├── AndroidManifest.xml    ← INTERNET, RECORD_AUDIO (runtime), POST_NOTIFICATIONS (runtime)
        ├── java/com/tolik/client/
        │   ├── TolikApiModels.kt        ← CONTRACT LAYER: dependency-free data classes +
        │   │                              TolikApiTransport (existing signatures preserved;
        │   │                              additive extensions: health/agents/confirmations/
        │   │                              voice SSE/events-WS/bootstrap)
        │   ├── OkHttpTolikTransport.kt  ← transport impl: Bearer per request, 401/403 →
        │   │                              failure (never local auth, no silent retries),
        │   │                              SSE parser, /events/ws socket with bounded backoff
        │   ├── SessionStore.kt          ← private prefs for base URL + session token
        │   │                              (excluded from backup/device transfer, SECURITY §6)
        │   ├── DeviceIdentity.kt        ← AndroidKeyStore EC P-256 keypair (non-exportable,
        │   │                              SECURITY §3/§11) + installation UUID
        │   ├── VoiceClient.kt           ← WavWriter (16 kHz mono PCM16 → WAV),
        │   │                              AmplitudeVadGate (M6 mirror), UtteranceSegmenter
        │   │                              (0.10 port: pre-roll 250 ms / end-silence 700 ms /
        │   │                              hard cap 10 s), MicCaptureEngine (AudioRecord),
        │   │                              TtsChunkPlayer (sequential MediaPlayer)
        │   ├── VoiceSessionController.kt← turn orchestration: mic → gate → /voice/turn-stream
        │   │                              SSE → deltas + sequential audio playback;
        │   │                              barge-in = abort + POST /voice/cancel (M5);
        │   │                              turn-level echo gate + 400 ms tail (0.10 live fix);
        │   │                              speaker hints displayed, never authorization (§13)
        │   ├── TaskTracker.kt           ← client-side task registry (Core has no list
        │   │                              endpoint: created tasks + WS events + GET per task)
        │   ├── TaskAdapter.kt           ← task rows with cancel/confirm buttons
        │   ├── ChatAdapter.kt           ← user/TOLIK/error bubbles
        │   ├── TolikNotifier.kt         ← system notifications (channels + runtime permission)
        │   └── MainActivity.kt          ← single Activity: chat / voice / tasks tabs, status
        │                                  header, settings (dev token + enroll), confirmation
        │                                  dialogs, WS event wiring
        └── res/
            ├── layout/activity_main.xml     (header + 3 screens + bottom nav)
            ├── layout/item_chat_message.xml
            ├── layout/item_task.xml
            ├── menu/bottom_nav_menu.xml
            ├── values/strings.xml           (Russian UI strings, app name "TOLIK")
            ├── values/colors.xml
            ├── values/themes.xml            (Material 3, light)
            └── xml/backup_rules.xml, data_extraction_rules.xml, network_security_config.xml
```

## Core surface used (every call matches `src/TOLIK.Core.Host/Program.cs`)

| Endpoint | Auth | Notes |
|---|---|---|
| `GET /health` | none | status header (core/voice/agent/memory/locked) |
| `GET /status` | Bearer | role/userId/deviceId (`TolikStatus`) |
| `GET /agents` | Bearer | connection state for the header |
| `GET /auth/local-token` | none (dev-only) | loopback + `TOLIK_DEV_AUTO_TOKEN`, blocked after Genesis; 404 → surfaced |
| `POST /auth/enroll` | none | enrollment token from the Owner; device identity (AndroidKeyStore pubkey + installation UUID) |
| `POST /chat` | Bearer | body `{message}`; reply in `answer` |
| `POST /tasks` | Bearer | body `{description}`; 202 Accepted with the task |
| `GET /tasks/{id}` | Bearer | fresh status |
| `POST /tasks/{id}/cancel` | Bearer | 409 on invalid state → surfaced |
| `GET /tasks/{id}/confirmations` | Bearer | pending confirmation list |
| `POST /tasks/{id}/confirmations/{cid}/approve` | Bearer | approve; 403 (owner role / MFA) → surfaced, never bypassed |
| `POST /voice/turn-stream` | Bearer | body `{audio: <base64 WAV>, mode, runtimeMode}`; response is JSON (intent path) or SSE (`delta`/`audio`/`done`/`error`) |
| `POST /voice/cancel` | Barge-in | abort + explicit cancel of the caller's own turn (M5) |
| `GET /events/ws?token=…` | WS token query | `hello` + `event` payloads (`taskId/kind/detail/occurredAt`, `proactive_reminder`); reconnect with bounded backoff, no retry on 401 |

## Contract notes / deviations found in the Core surface

1. **No `GET /tasks` (list) endpoint** — the task list is client-side: tasks
   created by this client + per-user WS events + `GET /tasks/{id}` refreshes
   (`TaskTracker`).
2. **No `POST /tasks/{id}/confirm` route** — the pre-existing
   `TolikApiTransport.confirmTask(taskId)` is implemented as the composite
   "list confirmations → approve the first pending one" over the two real
   endpoints; deny = do not approve (no deny endpoint exists).
3. **`/chat` reply field is `answer`** — `TolikChatResponse` gained `answer`
   additively (old fields kept; `.text` resolves whichever is present).
4. **`/voice/turn-stream` returns EITHER JSON or SSE** (Content-Type decides) —
   both handled; SSE audio chunks are played sequentially via MediaPlayer
   (container-agnostic, WAV/MP3).
5. **Voice barge-in during playback is NOT attempted without AEC** — the
   visible cancel button is the ESC-analog; the turn-level echo gate (whole
   turn + 400 ms tail) mirrors the 0.10 live fix.

## Security invariants (SECURITY & TRUST MODEL)

- Session token: Core-issued at runtime via the dev flow or enrollment; stored
  only in private prefs excluded from cloud backup and device transfer.
- Device identity: AndroidKeyStore keypair — copying app data does not carry
  usable credential material.
- 401/403 → failure surfaces (chat error bubble, toast, header); no silent
  retry-forever loops; WS reconnect skips retries on 401.
- WS/HTTP payloads are untrusted data — they refresh views, never grant
  anything (§22/§23).
- Speaker verification is a probabilistic hint only, never authorization (§13).
- RECORD_AUDIO and POST_NOTIFICATIONS are runtime-requested with rationale,
  never silent.
- Transport security: cleartext is allowed for the home-LAN/loopback dev phase
  only (`xml/network_security_config.xml`) — must be tightened to HTTPS pins
  before any remote/VPS deployment (AGENTS.md §28).
