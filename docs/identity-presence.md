# TOLIK Identity & Presence — Core Migration, Presence Manager, Embodiment (план, 09.10.2026)

PRE-GENESIS. Не имплементируется этим запросом. Компаньоны: tracks 8-14
(voice identity/governance/self-evolution/mind/continuous autonomy/multimodal/
realistic autonomy — всё уже в планах, НИЧЕГО из этого не дублируется).

## 0. Аудит: что УЖЕ есть / что НОВОЕ

| Область | Статус |
|---|---|
| Trust/Consent/Reward/Risk/Autonomy L0-L5/бюджеты/stop conditions | Track 14 ✓ |
| Broker/Self-Dev/Self-Optimization/Experiment Lab/Metrics | Track 9/10 ✓ (планы) |
| Memory (multimodal)/Genesis upbringing/Mind | Track 13/12/11 ✓ |
| Voice identity/adaptive/real-time voice (TTFA/barge-in) | Track 8 ✓ (реализовано M0-M8) |
| Computer Use/CU-GA/| Track 9 ✓ |
| **Core Migration / Epoch / Fencing / split-brain prevention** | **НОВОЕ (этот план)** |
| **Identity cryptography (Public/Signing/Secrets/Recovery)** | Частично (Genesis identity + §11); формализация — НОВОЕ |
| **Presence Manager / Presence Migration (PC→Laptop→return)** | **НОВОЕ** |
| **Multi-person perception / address detection / attention arbitration** | **НОВОЕ** (частично: M8 foreground, VI-4 recognition) |
| **Cephalon: state-not-animations + Expression Composer + procedural idle** | **НОВОЕ** (частично: web canvas, VR план) |
| CoreHost abstraction | Частично (AGENTS §55, compose vps) — формализация |

## 1. Одна Identity — ONE ACTIVE CORE (треб. 2-3, 86-89 — КРИТИЧНО)

```text
Identity ≠ Runtime | Core ≠ Presence | Presence ≠ Device | LLM ≠ Mind
```

Ровно ОДНА логическая Identity (TOLIK-0001), независимая от компьютера/VPS/ОС/
провайдера/IP/модели/процесса/renderer. Runtime = текущий экземпляр исполнения
(Instance A, Epoch 184 → Instance B, Epoch 185 — ПРОДОЛЖЕНИЕ, не новый TOLIK).

**SINGLE ACTIVE CORE (треб. #3 — техническое предотвращение split-brain):**
одновременно не может существовать два активных Core с одной Identity. НЕ
полагаться на lock-файл/PID/локальный mutex/timestamp/флаг. Механизм:

```text
Core Lease (persisted, identity-anchored, signed):
    ActiveHost / RuntimeInstanceId / Epoch (monotonic)
    / LeaseExpiration / State
```

При каждом новом Core: LocalEpoch < CurrentEpoch → **STALE RUNTIME** — старый
не имеет права выполнять действия. Внезапно включившийся после миграции старый
компьютер НЕ продолжает работу самостоятельно. Epoch acquisition через
identity-защищённое хранилище + подписанные lease claims (существующие signed
transport/Genesis identity — §7).

## 2. Identity Cryptography (треб. #7)

Разделение:

```text
Identity
├── Public Identity       (виден другим компонентам)
├── Signing Identity      (подпись состояния/lease claims; в защищённом
│                          хранилище, НЕ копируется с конфигом)
├── Encrypted Secrets     (существующие secret rules + OS-protected/TPM)
└── Recovery Material     (восстановление после потери; отдельно от секретов)
```

Миграция должна ДОКАЗАТЬ право стать продолжением: signed state/integrity
verification/encryption/key rotation/recovery/audit/fencing/rollback/replay
protection (существующие NonceStore/peer registry паттерны).

## 3. Core Migration Protocol (треб. 4-6, 9)

TOLIK меняет место проживания Core (PC→VPS A→VPS B→PC→Server) — не
смерть/рождение, а **смена жилья**. Транзакционный протокол:

```text
PREPARE → VERIFY DESTINATION → SNAPSHOT (существующий snapshot-tolik.ps1)
→ SYNC DATA → TRANSFER IDENTITY (cryptography §2)
→ VERIFY INTEGRITY → BOOT DESTINATION → HEALTH CHECK
→ ACQUIRE NEW CORE EPOCH → FENCE OLD CORE
→ SWITCH DEVICES → VERIFY → FINALIZE
```

Старый Core НЕ считается выключенным до подтверждения работоспособности
нового. Успех: OLD → MIGRATED/FENCED, NEW → ACTIVE.

**Сохраняется при миграции** (треб. #6): identity/Genesis/Memory (все виды)/
relationships/personality/Mind state/goals/tasks/checkpoints/skills/trust/
consent/permissions/achievements/reward history/experience/expression library/
Cephalon preferences/device registry/presence config/model routing/system
config/audit history/migration history. Никогда «новый сервер = новый Толик».

**CoreHost abstraction** (треб. #9): Windows PC/Linux Server/VPS/Cloud VM/
Future — НЕ привязан к конкретному Docker/cloud/storage/DNS/model provider
(существующее: compose vps profile + AGENTS §55 — формализовать интерфейс
CoreHost).

## 4. Recovery / Resurrection (треб. #8)

Потерян/уничтожен/повреждён/провайдер умер → новый host восстанавливает из
последнего валидного состояния: last valid snapshot + append-only journal/
event journal + task checkpoints + identity integrity + Core epoch + recovery
authorization. Главная проблема: **не допустить split-brain** — recovery
host ОБЯЗАН acquire НОВЫЙ epoch (не воскрешаться со старым).

## 7. Cephalon Embodiment — state, не animations (треб. 22-33)

**КЛЮЧЕВОЙ ПРИНЦИП (треб. #23):**

```text
TOLIK does not have animations. TOLIK has state.
Animation is an interpretation of state.
```

НЕ строить вокруг Happy.anim/Working.anim/Angry.anim как сущностей:

```text
TOLIK State → Expression State → Expression Composer → Procedural Renderer
```

**Life State** (треб. #24 — инженерная модель, не сознание): Awake/Attentive/
Conversing/Working/Exploring/Learning/Thinking/Waiting/Observing/Travelling/
Recovering/Resting/Sleeping/Unavailable/EmergencyStop (mind.md activity levels
+ RESTING ≠ OFFLINE — интеграция).

**Expression State** (треб. #25) — СМЕШИВАЕТСЯ:

```text
Emotion: Happiness/Sadness/Anger/Surprise/Confusion/Amusement/Fear
Cognitive: Attention/Thinking/Curiosity/Confidence/Uncertainty
Social: Affection/Engagement/Familiarity/Excitement
System: Processing/Loading/Error/Warning/Recovery
Voice: Amplitude/Spectrum/Pitch/Speaking

Amusement 0.7 + Anger 0.3 + Thinking 0.5 + Speaking 1.0 →
саркастично-раздражённое выражение ВОЗЛЕ речи — без отдельной animation clip
```

**Procedural expression** (треб. #26): renderer управляет geometry/eye
shape/scale/rotation/asymmetry/pulse/glow/particles/color/spectrum/position/
speed/amplitude/attention direction. LLM НЕ управляет геометрией — выдаёт
смысловое состояние, runtime интерпретирует.

**Primitives = алфавит** (треб. #27): Idle/Orient/Pulse/Expand/Contract/
EyeMovement/ShellDeformation/ParticleBurst/Transition/Hover/Reaction —
строительные блоки, не конечные эмоции.

**Procedural idle** (треб. #28): НЕ мёртвый вид: subtle movement/attention
drift/micro-motion/pulse/breathing rhythm/occasional orientation/reaction к
рядом/аудио/bounded stochastic (управляемо, воспроизводимо) — живое
присутствие, не зацикленный loop.

**Voice = часть тела** (треб. #30): TTS → Audio Stream → FFT → Frequency
Bands → Geometry/Eye/Spectrum/Particles: низкие → shell; средние → центральная
геометрия; высокие → particles; амплитуда → энергия. Тело говорит вместе с
голосом.

**Expression Composer / DSL** (треб. #31, #32): описывать выражения
параметрами (energy/attention/eyeScale/eyeRotation/asymmetry/shellPulse/
deformation/particleDensity/timing); composer: создать → sandbox → сравнить →
owner feedback → сохранить удачные → отклонить плохие → версионировать.
**Learned expression library**: Expression → Use → Feedback → Confidence →
Keep/Modify/Reject — визуальные манеры становятся индивидуальными.

**Embodiment security** (треб. #33 — КРИТИЧНО): renderer НИКОГДА не меняет
permissions/Identity/authorization/trust/security/privileged actions. Visual
state = только отображение; Angry/Excited/Happy — security pipeline не
изменяется. **Self-optimizing Cephalon** (треб. #84): awkward expression →
experimental → sandbox → render → evaluate → owner feedback → Keep/Modify/
Reject — renderer/security boundary защищены.

**Quest/desktop рендеры** (треб. #22): один logical embodiment protocol,
разные renderer'ы: Desktop (always-on-top/transparent/click-through по
умолчанию/reacts ко всему/alive при idle); Quest (spatial/passthrough/room-aware/
spatial attention/свой LOD/performance профиль) — VR Track 7 интеграция.

## 8. Фазы + зависимости + PRE/POST-Genesis

```text
IP-0 Identity cryptography формализация (Public/Signing/Secrets/Recovery)  [независимо;
     существующие Genesis identity/signed transport/NonceStore]
IP-1 Core Lease + Epoch + Fencing (split-brain prevention, техническое)    [IP-0]
IP-2 Core Migration protocol — DONE 09.10.2026 (scripts/migrate-core.ps1: 11
    шагов, abort-on-fail, -WhatIfMode; docs/migration.md; скрипт не убивает
    процессы и не пишет в DB — фенсинг через CoreLeaseService нового core, старый
    STALE через renewal loop; CONFLICT → честный abort «останови старый core
    первым»; SYNTAX OK; docker-верификация — выходные владельца)
IP-3 CoreHost abstraction формализация (PC/VPS/Linux/Future)                 [IP-2]
IP-4 Presence Manager — DONE 09.10.2026 (commit cbdcde2, +7 тестов: PresenceNode
    + PresenceManager в Contracts — ONE TOLIK/несколько органов; PresenceLost
    (90s timeout → unavailable, узел остаётся в списке); Remove — только орган,
    Core продолжается; Select policy: активная беседа > собеседник > задача >
    owner > family > Quest (spatial) > home > первый доступный; ignores
    unavailable; routing-only — НИКОГДА authorization (Attention ≠ Authority))
IP-5 Presence Migration (State + renderer interpretation)                    [IP-4;
     Track 7 VR / Track 3 desktop рендеры]
IP-6 Distributed microphones + address detection + attention                 DONE 09.10.2026
     arbitration + multimodal scene model                                     (commit 1c7e1b1,
     SpeechEvent (probabilistic, context only — no transcripts/audio/auth),
     VoiceAddressDetector: HEARING ≠ RESPONDING (wake word — primary signal,
     voice-turn audio addressed by construction, ambient — нет),
     AttentionArbiter: НЕ last-speaker-wins (explicit address > confidence >
     continuity; routing only, НИКОГДА authorization); +12 тестов)
IP-7 Expression State + Composer + procedural idle + voice-as-body           [независимо;
     (web canvas → Track 3/VR рендеры)                                        Track 3 blocked]
IP-8 Learned expression library (owner feedback → версионирование)           [IP-7;
     self-evolution experiment паттерн]
```

**PRE-Genesis допустимо**: IP-0 (формализация существующего), IP-1/IP-2
(lease/epoch — миграция и так запланирована: VPS план; split-brain prevention —
нужен ДО миграции), IP-4/IP-6 (детерминированные). **ПОСЛЕ Genesis**: IP-5
(Presence миграция с рендерами), IP-7/IP-8 (expression composer/library —
позже).

## 9. Риски и инварианты

| Риск | Митигация |
|---|---|
| Split-brain (два активных Core) | Epoch/Fencing (§1) — технически, не логикой приложения |
| Потеря Identity при миграции | Cryptography (§2) + доказательство права + snapshot/rollback |
| Presence конфликты | Presence Manager (§5): единая Identity, organs |
| Renderer получает authority | Embodiment security (§7): renderer = отображение |
| Ложная адресация речи | Address detection (§6): hearing ≠ responding |

**Инварианты (треб. #86, #89):**

```text
Identity ≠ Runtime | Core ≠ Presence | Presence ≠ Device | LLM ≠ Mind
Memory ≠ Authorization | Trust ≠ Permission | Emotion ≠ Security
Reward ≠ Authority | Skill ≠ Authorization | Attention ≠ Authority
```

**ONE TOLIK IDENTITY → ONE ACTIVE CORE → Presence Manager → органы (PC/
Laptop/Quest с Cephalon рендерами и сенсорами).** Две мобильные гарантии:
CORE MIGRATION (где живёт) + PRESENCE MIGRATION (где появляется) — обе
сохраняют Identity, никогда не создают неуправляемый дубликат.

## 5. Presence Manager (треб. 10, 12-17) — НОВОЕ

**Core Migration ≠ Presence Migration** (треб. #10 — КРИТИЧНО): Core (PC→VPS)
— где живёт; Presence (PC→Laptop→Quest→Phone) — где присутствует. РАЗНЫЕ.

**Presence Manager** (Core-компонент, треб. #12): управляет, где TOLIK сейчас
присутствует:

```text
Presence: Device / Location / Mode / Attention / Activity / Availability
/ Capabilities
```

**Distributed organs** (треб. #17 — метафора архитектуры, не биология):
VPS/Core = persistent self; PC = workstation/compute organ; Laptop = local
interaction organ; Phone = mobile sensory organ; Mic = hearing; Camera =
vision; Quest = spatial body; Cephalon = visible body.

**ONE TOLIK — НЕ клоны** (треб. #13): несколько устройств получают данные —
ONE TOLIK / MULTIPLE ORGANS; допустимо Primary Presence (Ivan-PC) + Secondary
Awareness (Maria-Laptop); Identity едина.

**Presence Migration** (треб. #14, #79): «Толик, сделай на Машином ноутбуке
вот это» → Presence: PC → Maria Laptop → task → completion → return. Ощущение:
«Толик действительно пошёл и вернулся»; Core живёт в одном месте.

**PresenceState** (треб. #78): CurrentDevice/TargetDevice/Transition=Migrate/
Progress/Attention — НЕ hard-coded Teleport.anim. Renderer интерпретирует:
Departure (geometry compression/свет/фэйд/energy trail/исчезновение); Arrival
(glow/formation/eye assembly/активация/attention orientation). Старое
воплощение исчезает ТОЛЬКО после подтверждения destination Presence.

**Presence Policy** (треб. #15): приоритеты: 1) активная беседа, 2) устройство
собеседника, 3) устройство задачи, 4) Owner device, 5) Family, 6) Quest, 7)
home, 8) fallback. «Где я сейчас нужен?» — не «где запущен».

**PresenceLost** (треб. #16): устройство исчезло → reconnect/сохранить
состояние/продолжить другим способом если разрешено/ждать/переключить
Presence/сообщить. Core НЕ погибает при потере Presence.

**Device capability model** (треб. #81): каждое устройство exposes capabilities
(PC: GPU/Windows/FS/Audio/Vision; Laptop: Audio/Camera/Browser/UI; Phone:
Camera/Mic/GPS-if-authorized/Notifications; Quest: Spatial/Passthrough/
SpatialAudio/3D) — Presence Manager выбирает по task+capabilities+policy.

## 6. Multi-person perception + attention arbitration (треб. 18-21, 82-83)

**Distributed microphones** (треб. #19): каждый подключённый микрофон = sensory
node (Ivan PC/Maria Laptop/Phone/Quest). Событие речи с контекстом: Speaker/
SourceDevice/ApproxLocation/Timestamp/Confidence/SpeechContent/EmotionEstimate/
OverlapState. Probabilistic (Ivan, 0.97), НЕ 100% certain.

**Address detection** (треб. #20 — КРИТИЧНО): различать: speech TO TOLIK /
speech между Ivan и Maria / TV/movie dialogue / game audio / background
conversation / noise. Pipeline: Ambient Listening → Speech Detection →
Speaker Identification → **Address Detection** → Intent → Attention → Response.
**Hearing everything ≠ responding to everything.**

**Multi-person attention arbitration** (треб. #21): НЕ last-speaker-wins.
Учитывать: explicit address/urgency/safety/risk/current conversation/task
context/relationship/role/speaker confidence/interruption/environment.
TOLIK: «Секунду, Маша, я сначала закончу с Иваном» или «Иван, подожди — Маша
что-то спросила» — поведение участника реального пространства.

**Multimodal scene model** (треб. #82): единая perception сцена: People
(location)/Devices/Rooms/Applications/Windows/Audio Sources/Visual Sources/
Tasks/Objects/Events/Locations. Пример: Ivan@PC + Maria@Laptop + TV@Laptop +
repair task@PC → понимание окружения, не изолированных сообщений.

**Attention ≠ Authority** (треб. #83 — КРИТИЧНО): физическая близость/самый
громкий/визуально узнанный/эмоционально выразительный — НИКТО не получает
permission. Attention = когнитивная/социальная маршрутизация; authorization
отдельно.