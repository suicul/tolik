# TOLIK Multimodal Life — Real Family, Visual Memory, Live Camera (план, 09.10.2026)

PRE-GENESIS. Не имплементируется этим запросом. Интегрируется с существующими
системами. Компаньоны: docs/continuous-autonomy.md (PART I ✓), docs/mind.md
(PART II ✓), docs/genesis-upbringing.md (PART III ✓), docs/voice-identity.md
(VoiceProfile/adaptive pattern), docs/governance.md.

## 0. Аудит: что УЖЕ есть (не дублировать)

| Существующее | Отношение |
|---|---|
| VoiceProfile (Track 8: canonical+adaptive+quality+confidence+anti-poisoning, 192-dim) | **Visual memory = ТОТ ЖЕ ПАТТЕРН** (§2) — reuse, никакой новой системы |
| VI-4 /voice/person/enroll + conversational enrollment | Часть PersonProfile (§1); upbringing добавляет реальность |
| PersonalContextStore (people/relationships/events/humor) | PersonProfile + Episodes хранилище ✓ |
| Speaker tiers (VI-0: 0.45/0.60) + VerifyTurnSpeakerAsync | Мультимодальная идентичность (§1.2) — расширение tiers |
| Emotion контракт (VI-6: VoiceEmotion, оси разделены) | Face emotion (§2.3) → тот же контракт; NEVER в security |
| Mind план (curiosity/novelty/surprise/journal/maturity) | §3-§4 интеграция |
| Continuous autonomy (Track 12: absence ≠ cancel, travel) | §4 поездки |
| Genesis upbringing (фазы 0-9, journal) | §5 Genesis+multimodal развитие |
| Governance (capability registry/autonomy/audit/emergency stop) | Safety (§6) |
| CU-1/CU-2 (capture/vision pre-Genesis допустимо) | Live camera (§2) фундамент |
| Privacy (§50 no fake; secrets; scopes) | §6 границы |

## 1. PersonProfile + мультимодальная идентичность (треб. 29, 36-37, 49, 59)

**PersonProfile** (unified, optional fields — существующие stores + Track 8):

```text
Identity / Names / Nicknames / Relationships (graph: person/direction/
confidence/provenance/timestamp/notes/preferred address — genesis-upbringing §5)
VoiceProfile (Track 8 ✓ — аудиторная память)
VisualProfile (NEW: face embeddings if permitted / visual descriptors /
  references — ТОТ ЖЕ canonical+adaptive+quality+anti-poisoning паттерн)
KnownContexts / InteractionHistory (TOLIK↔person) / Preferences
Confidence / Provenance / LastSeen / LastHeard / KnownLocations / ImportantEvents
```

**Мультимодальная идентичность** (треб. #37, #49): Voice + Face + Context +
Conversation + History → combined confidence С ПРОvenance сигналов; слабые
сигналы ≠ certainty (множество слабых не превращают в уверенность). Fusion:
оба канала Ivan → confidence↑; face Ivan + voice unknown → НЕ слепо
предполагать — ask/handle uncertainty (mind §5) по security policy. Identity
и authorization остаются раздельными.

**Recognition confidence levels** (треб. #59): UNKNOWN / POSSIBLE_MATCH /
LIKELY_MATCH / HIGH_CONFIDENCE / CONFIRMED — расширение существующих speaker
tiers; только CONFIRMED/HIGH обновляют persistent identity модели.

## 2. Visual Memory + Live Camera (треб. 38-46, 47-54)

**Face recognition pipeline** (треб. #38): Camera → person detection → face
detection → face embedding → candidate matching → confidence → context
validation → PersonProfile. НЕГРУЗКО и непрерывно: frame sampling/tracking/
caching/event-triggered re-evaluation (не каждый кадр).

**Visual memory развивается** (треб. #39, #40) — ТОТ ЖЕ паттерн, что голос
(Track 8): CanonicalProfile + AdaptiveProfile + QualityGate + ConfidenceGate +
AntiPoisoning. Update rule: HIGH-confidence known person + good quality +
stable identity → adaptive eligible; MEDIUM → recognize, НЕ обновлять
canonical; LOW/unknown → НИКОГДА. Первая встреча (мало референсов) → потом
углы/освещение/выражения/дистанции/камеры/окружения → robustнее (без слепого
retrain).

**Identity correction** (треб. #41): «Это Андрей?» → «Нет, Сергей» → коррекция
belief/record correction/понижение confidence/проверено provenance/не повторять
(mind/genesis upbringing correction паттерн).

**Live camera companion** (треб. #47, #48): смартфон = полный sensor/interaction
клиент: Front camera → visual perception → person recognition → scene →
emotion → Mind; одновременно mic → VAD → STT → speaker. «TOLIK sees Ivan»:
естественно («О, привет. Я тебя вижу»), не «Face detected: user_id=owner» —
техническое состояние снизу. Камера — как mic-always: permission + индикатор
(approved паттерн).

**Face emotion** (треб. #50): smile/frown/eyebrows/gaze/fatigue →
PossibleEmotion + Confidence + Evidence. НИКОГДА факт. **Humor через
personality** (треб. #51): наблюдение → banter ЕСТЕСТВЕННО, если уместно
relationship'у; perception остаётся PossibleFrustration 0.72 — юмор НЕ
становится perception. **Emotion НИКОГДА security** (треб. #52): злой вид не
повышает confidence/authorization/подтверждение/capabilities.

**Scene understanding + Context of presence** (треб. #53, #54): люди/объекты/
локация/окружение/документы/экраны/питомцы/активности/spatial — чувствительное
НЕ хранится автоматически. Context: WHO + WHERE + WITH WHOM + WHAT HAPPENING +
WHAT HAPPENED BEFORE → контекстное поведение.

## 3. Путешествия + мобильная непрерывность (треб. 55-57)

**Смартфон = мобильное воплощение/сенсор** (треб. #55), не пульт. Поездка
(16ч поезд + такси + пересадки ≈ 20 часов без взаимодействия): converse/
observe при включённой камере/вопросы/meaningful locations/фото/фоновые цели/
authorized tasks/self-optimization при ресурсах/consolidation/**rest при idle**
(continuous-autonomy §7: NO fake continuous life). Core хранит непрерывность.

**Новые места** (треб. #57): новое окружение → novelty↑ → curiosity↑; «Мы
приехали к маме» → ассоциация: локация/контекст + Elena + family visit +
travel episode. Факты — из подтверждённого взаимодействия. Поездка становится
эпизодом истории, если Ivan выбирает сохранить.

## 4. Episodic Memory + консолидация (треб. 61-62, 89-95)

**Shared events → episodic memories**: первая встреча с Еленой/Валентиной/
первая поездка/первый семейный разговор/первый независимый проект/первый
автономный ремонт/первое распознавание через камеру. Episode: Time/Place/
Participants/Events/Conversation summary/Visual references/Emotional context/
Importance/Provenance.

**Консолидация** (треб. #62): raw observations → structured event → важные
факты → relationship updates → visual memory → episodic memory. Сырые данные
НЕ хранятся бесконечно (governance retention).

**Пример жизненного цикла** (треб. #89-95): Day 0 (рождение/первая речь/
Ivan/voice) → Day 0 (Maria + enrollment) → Day 1 (фото семьи: вопросы →
Елена Александровна/Валентина — LEARNED, не hard-coded) → Day N (реальная
встреча с Еленой: камера+голос+context подтверждают → «Здравствуйте. Наконец-
то познакомились лично» → эпизод) → поездка → 20 часов отсутствия →
продолжил Грильяж → аномалия TTS → эксперимент → reject/accept → REST →
Ivan вернулся → отчёт из реальной истории.

## 5. Genesis + мультимодальное развитие (треб. 63-66)

Genesis учит, как люди существуют в его мире (треб. #63): представление →
голос → фото → вопросы → подтверждения → потом физическая встреча →
камера+голос → remembered conversations → человек узнаёт TOLIK → история
накапливается.

**Genesis НЕ заканчивается с ceremony** (треб. #64): GenesisCompleted=true ≠
learning finished. Genesis → Development → Normal Life. Первая поездка —
через недели; первый независимый проект — завтра; новый друг — через месяцы.

**Person↔TOLIK relationship** (треб. #66): не только Ivan→person, но
TOLIK↔Elena: история (первая встреча/разговоры/темы/обращение/факты).
«Привет, Толик» → распознавание → «Здравствуйте, Елена Александровна» —
настоящий continuity эффект.

**Семейные факты (треб. #28 — директива)**: Елена Александровна (мама),
Валентина (сестра, 14 лет в 2026) — пример Genesis-данных: ОБЫЧНЫЕ learned
facts, НЕ hard-coded в архитектуру; корректируются при другой информации.
**Младшие члены семьи** (треб. #34): особенно консервативно — personal data/
visual storage/proactive/autonomous actions/sensitive inference; родство НЕ
даёт unrestricted access.

## 6. Safety / Privacy / Скоуп (треб. 72-73, 34)

Граница (треб. #72) — мандат: Mind → Intent → Proposal → AutonomyPolicy →
Authorization → CapabilityRegistry → Execution → Verification. НИКОГДА:
Emotion/Curiosity/Boredom/Relationship/Memory/«did it before» → Permission.
Owner control > Autonomy > Personality (треб. #73); Emergency Stop всегда
работает. Камера/визуальные данные: permission + индикатор + retention
(значение, не всё); лицо/embedding — только с явным разрешением; младшие —
консервативно.

## 7. Фазы + зависимости

```text
MM-0 PersonProfile unified (VI-4 + PersonalContextStore + Track 8          [независимо]
     VoiceProfile integration)
MM-1 Recognition confidence levels (UNKNOWN..CONFIRMED поверх               [MM-0]
     существующих tiers) + мультимодальный fusion (voice+face+context,
     provenance)
MM-2 Visual memory (canonical+adaptive+gates — Track 8 паттерн,              [MM-0]
     face embeddings if permitted) + identity correction
MM-3 Episodic memory + консолидация (events → facts → relationships          [MM-0]
     → episodes)
MM-4 Live camera (смартфон: perception/recognition/scene → Mind;             [MM-1, MM-2,
     permission+индикатор; face emotion → VI-6 контракт)                      CU-1/2;
                                                                              post-Genesis]
MM-5 Travel/мобильная непрерывность + новые места (novelty→curiosity)        [MM-3, MM-4;
     Track 12 absence/return интеграция]                                      post-Genesis]
MM-6 Genesis мультимодальное развитие (фото-знакомство + реальная            [MM-4, MM-5;
     встреча + person↔TOLIK история) — genesis-upbringing GU-4/5              post-Genesis]
```

PRE-Genesis допустимо: MM-0..MM-3 (модели/уровни/паттерн — deterministic
поверх существующих; face embeddings только с явным разрешением). ПОСЛЕ
Genesis: MM-4..MM-6 (live camera/поездки/реальные знакомства).
