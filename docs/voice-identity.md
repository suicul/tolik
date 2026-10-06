# TOLIK Voice Identity — Genesis Enrollment + Adaptive Speaker Profile (план, 07.10.2026)

Долгоживущая система идентификации говорящих: со временем устойчивее к
естественным изменениям голоса, но НЕ допускает profile poisoning. Полностью
интегрируется в существующие Security/Identity/VoiceRuntime архитектуры —
никаких параллельных систем.

## 0. Текущее состояние (НЕ дублировать)

Уже существует и работает:

- **CampPlus embeddings**: voice service `speaker_verify.py` — ONNX 192-dim,
  unit-векторы, kaldi fbank (рецепт CosyVoice3), stateless, 503-деградация
  (Core трактует как «verification disabled»). Audio ≥1s.
- **SpeakerVerifier** (Core): чистый cosine; `AcceptThreshold = 0.45`
  (софт-порог: guest-mode prompt, приоритет доступности; §13: voice ≠
  authorization). Живая калибровка: тот же спикер ≈0.49–1.0, чужой ≈0.2–0.3.
- **Enrollment** `/voice/speaker/enroll`: аутентифицированная сессия → ≤5 клипов →
  **mean unit vector** → memory item `speaker.profile` (Private, per-userId);
  пере-enrollment заменяет. Только embedding — raw audio не хранится.
- **Turn flow** (/voice/turn, /voice/turn-stream): turn embedding → cosine vs
  profile[0] вызывающего → speakerVerified → guest-mode prompt +
  provenance `voice.user.unverified`; аудит ALLOW/DENY `voice.speaker.turn`.
- **Genesis** (`GenesisService`): preflight (owner auth, MFA, dev off, authority,
  backup/restore attestations, memory separation) → ceremony (instance identity +
  audit chain, атомарно, rerun-impossible). **Голосовой фазы НЕТ.**
- **Identity/roles**: SessionPrincipal(user, device, session, role);
  Owner/Family/TrustedGuest; enrollment-токены (Owner-minted, single-use, TTL 10
  мин); ceilings/minimum-role matrix (Codex/OpenCode/RunProcess → Owner).
- **M6 VAD** (speechMs — сигнал качества сэмпла), **M7 telemetry**.
- Тесты: SpeakerVerifierTests, VoiceTests, IdentityEvidenceTests,
  family/enrollment (15), GenesisCeremonyTests (6).

## 1. Gap analysis (против 12 требований)

| # | Требование | Статус |
|---|---|---|
| 1 | Genesis = voice enrollment владельца | GAP — ceremony без голосовой фазы |
| 2 | Adaptive profile после Genesis | GAP |
| 3 | Anti-contamination policy (tiers) | GAP — бинарный софт-порог |
| 4 | Bounded adaptation, decay, canonical anchor | GAP |
| 5 | Emotion ≠ Speaker identity | GAP — эмоций нет; нужен контракт |
| 6 | Conversational enrollment (Мария) | Частично: роли/токены есть, голосового потока нет |
| 7 | Speaker ≠ Authorization | **УЖЕ ТАК** (§13) — сохранить + покрыть тестами |
| 8 | Privacy/retention | В основном ✓ (только embeddings); добавить для adaptive |
| 9 | Recovery/диагностика/причины отказа | Частично (re-enroll есть); reset/diagnostics GAP |
| 10 | Realtime (не блокировать пайплайн) | Проектируемое ограничение для адаптации |
| 11 | 20 тестов | Новые |
| 12 | Household 1:N (кто из домашних говорит) | GAP — сейчас только 1:1 verify вызывающего |

## 2. VoiceProfile v2 — canonical + adaptive (треб. #2, #4)

Расширение существующего memory item `speaker.profile` (v1 = один embedding —
читается как canonical-only, обратная совместимость; апгрейд при пере-enrollment):

```json
{
  "version": 2,
  "model": "campplus",
  "canonical": [ "<unit vec>" x1..5 ],      // анкеры enrollment, ИЗМЕНЯЮТСЯ только
                                             // пере-enrollment'ом через security flow
  "adaptive": [ { "vector": [...], "addedAt": "...", "weight": 1.0 } ],  // cap M
  "canonicalMean": "...",                   // кэш быстрого сравнения
  "blendedMean": "...",                      // canonical-anchored blend (см. §3)
  "enrolledAt": "..."
}
```

Правила (v2 writer — детерминированные, unit-тестируемые):

- **Canonical защищён**: adaptive слой никогда не перезаписывает анкеры.
- **Bounded**: adaptive ≤ `AdaptiveSampleCap` (=8); при переполнении вытесняется
  образец с наименьшим weight×возраст (LRU-decay).
- **Aging**: образец старше `AdaptiveSampleTtlDays` (=30) исключается из blend
  (остаётся в истории до вытеснения) — голос «уходит» от старых состояний.
- **Blend**: `blendedMean = normalize(canonicalMean * Wc + adaptiveMean * (1-Wc))`,
  `Wc = 0.7` — СТАРТОВОЕ значение, калибруется в VI-0 на реальных данных
  (треб. #4: «не фиксировать числа» — калибровка по датасету review.tsv, 985 клипов).
- Сравнение на turn: `max(cos(turn, canonicalMean), cos(turn, blendedMean))` —
  canonical остаётся якорем, adaptive только расширяет принятие.
- **Rebuild**: adaptive слой выводим из canonical + набора подтверждённых
  embeddings заново (детерминированно).
- **Reset**: удаление adaptive без потери canonical (треб. #9, #15 тестов).

## 3. Confidence tiers + anti-contamination (треб. #3)

Три уровня, детерминированные, на ОДНОМ сравнении:

| Tier | Условие (старт., калибруется VI-0) | Identity | Adaptive update |
|---|---|---|---|
| HIGH | `max(cos) ≥ 0.60` И качество ок (VAD speechMs ≥ 2500) | подтверждена | **РАЗРЕШЁН** (кандидат) |
| MEDIUM | `0.45 ≤ max(cos) < 0.60` | используется (софт, как сейчас) | **ЗАПРЕЩЁН** |
| LOW | `< 0.45` | unknown/guest (как сейчас) | **НИКОГДА** |

Критический сценарий защиты («чужой похожий голос → ошибочная identity →
заливка в профиль») закрывается ТРОЙНО:

1. Adaptive-tier выше accept-tier (0.60 > 0.45) — ошибка верификации на 0.5
   НЕ даёт адаптивного обновления.
2. Adaptive идёт ТОЛЬКО в bounded adaptive-слой (≤8, decay) — canonical анкеры
   не затрагиваются: даже серия ошибок не сдвигает якорь.
3. Household distinctness (§5): профиль не может быть создан/адаптирован,
   если он коллидит с чужим существующим профилем (cos ≥ 0.45 к другому
   профилю → отказ, диагностика).

Drift/poisoning математика: даже при Wc=0.7 и 8 «отравленных» adaptive сэмплах
blendedMean отклоняется ограниченно; сброс adaptive восстанавливает pristine
canonical. Покрывается тестами #3, #13, #14, #15.

## 4. Genesis: обязательный Owner Voice Enrollment (треб. #1)

Genesis Ceremony расширяется фазой 2 (та же MFA-сессия владельца; ceremony
НЕ завершена без голосового профиля):

```text
Genesis
→ Preflight (существующие гейты)
→ Ceremony (instance identity + audit chain)      [существующее]
→ ФАЗА 2: Owner Voice Enrollment                  [новое]
   → 5 фонетически различных фиксированных фраз (RU-панграммы:
     шипящие/свистящие/глухие/звонкие/р-л-контраст покрыты)
   → клиент записывает фразу → POST /voice/identity/enroll/sample
   → качество НА КАЖДЫЙ сэмпл: VAD speechMs ≥ 2500, embedding извлечён,
     cosine к уже собранным ≥ 0.30 (внутренняя консистентность —
     тот же говорящий)
   → при провале: конкретная фаза повторяется (не весь набор)
   → ≥4 из 5 успешны → canonical profile создан (speaker.profile.v2)
   → ceremony event: voice_enrollment_completed; иначе ceremony остаётся
     в состоянии voice_enrollment_pending (интерфейс показывает, ЧТО не так)
```

Эндпоинты (Owner+MFA-gated, существующие audit/patterns):

- `POST /genesis/voice/phrase/{n}` — следующая фраза для записи.
- `POST /genesis/voice/sample` — сэмпл → качество → накопление.
- `POST /genesis/voice/complete` — финализация canonical (≥4) → событие.

Raw audio нигде не сохраняется (существующее поведение) — только embeddings.

## 5. Conversational enrollment доверенных лиц (треб. #6, #7)

После Genesis владелец добавляет людей голосом. Переиспользуются
существующие identity-механизмы — НОВЫХ auth-путей не создаётся:

```text
Иван (Owner, голос HIGH-confidence verified): «Толик, это Мария, запомни её голос»
→ НОВЫЙ интент EnrollTrustedPerson (IntentRouter, deterministic)
→ ГЕЙТ: текущий turn speakerVerified==true И principal.Role==Owner
  (незнакомец НЕ может «запомнить себя как Ивана» — голос даже не
   верифицирован против его же профиля, а роль не Owner)
→ enrollment-сессия (TTL 5 мин, target name, роль = Family — НИКОГДА Owner,
  треб. #7; owner может указать TrustedGuest)
→ TOLIK голосом просит 2–5 фраз (обычный voice loop)
→ каждый сэмпл: качество (speechMs) + консистентность множества
  (попарный cos ≥ 0.30) + distinctness от ВСЕХ существующих профилей
  (cos < 0.45 к каждому — иначе «голоса слишком похожи»)
→ найден/создан Users-запись «Maria», role Family (существующая модель ролей;
  permissions от роли, НЕ от голоса — §13)
→ canonical profile создан под Maria userId
→ аудит: family.voice.enrollment (owner-инициированный)
```

Archитектурный pipeline (треб. #7 — уже соответствует, фиксируется тестами):

```text
Audio → VAD → Speaker Verify/Identify (WHO) → Identity hint
→ Session auth (ЧТО РАЗРЕШЕНО — голос никогда не выдаёт разрешения)
→ Role → Ceiling → Intent → Action
```

## 6. Household identification 1:N (треб. #6, #12)

Расширение turn-проверки: при MISMATCH с профилем вызывающего — скан ВСЕХ
домашних профилей (best match ≥ 0.45):

- Best match = другой человек (Мария) → `speakerHint = Maria` → guest-mode
  путь (как сейчас), НО персона/память используют контекст спикера; ответ
  адресован Марии; авторизация — по-прежнему сессия устройства (§13/§37).
- Никакого match → unknown (как сейчас).
- Никогда не «переключает» авторизацию сессии — только контекст ответа.

## 7. Emotion ≠ Speaker Identity (треб. #5)

Контракт (без фейковой модели): `SpeakerVerificationResult` расширяется
полем `Emotion` (enum: Neutral/Excited/Angry/Happy/Tired, default null =
детектор не сконфигурирован). Pipeline: emotion вычисляется ОТДЕЛЬНО и
никогда не влияет на speaker score. Будущий детектор (voice service, модель
выбирается отдельно) подключается к готовому контракту. Cephalon Runtime
потребляет `Ivan+Angry → красный`, `Maria+Happy → ...` независимо от identity.

## 8. Privacy / storage / retention (треб. #8)

- Только embeddings — существующее поведение, сохраняется для adaptive.
- Raw audio enrollment-сэмплов не хранится.
- Adaptive: cap 8 + TTL 30 дней + LRU-decay (бесконечной истории нет).
- Удаление профиля целиком: существующий memory delete + endpoint
  `/voice/identity/delete` (Owner; для своего — сам).
- Reset adaptive: `/voice/identity/adaptive/reset` — canonical сохраняется.
- Голоса unknown НИКОГДА не пишутся ни в один профиль (tier LOW, §3).
- Scope: профили Private к своему userId (существующее).

## 9. Recovery / диагностика (треб. #9)

- Re-enrollment владельца: существующий flow + security (MFA-сессия).
- Re-enrollment доверенного: owner-инициированный, тот же conversational flow.
- `POST /voice/identity/adaptive/reset` (Owner либо свой профиль).
- `POST /voice/identity/adaptive/disable` / `enable` (глобальный тумблер; Core
  config + audit).
- `GET /voice/identity/diagnostics` — canonical count, adaptive count/age/weight,
  last similarity, threshold margins, model, enrolledAt.
- Причина отказа верификации: turn response несёт `speakerTier`
  (high/medium/low/none) + `speakerReason` (below_threshold | quality_low |
  no_profile | service_disabled) — клиент показывает ПОЧЕМУ.
- Все изменения профиля — через существующие auth/audit механизмы.

## 10. Realtime (треб. #10)

Turn-путь не меняется по стоимости: embed уже считается (300ms live), сравнение
— max двух cosine (микросекунды). Adaptive update — ASYNC: HIGH-confidence turn
кладёт candidate в in-process channel; `VoiceAdaptationService`
(BackgroundService) обновляет профиль в фоне; очередь bounded (drop при
переполнении); никогда не блокирует VAD→verify→STT→intent→LLM→TTS.

## 11. Реализация: фазы, зависимости, порядок

```text
VI-0 КАЛИБРОВКА (offline, датасет review.tsv 985 клипов):
    same-speaker/different-speaker распределения cos по CampPlus;
    подтвердить/уточнить: Accept 0.45, AdaptiveTier 0.60, Wc 0.7,
    quality speechMs ≥ 2500, консистентность 0.30, distinctness 0.45.
    Скрипт + документированные числа. Блокирует под числами VI-1..VI-4.

VI-1 VoiceProfile v2 (Core): модель + reader/writer + v1-compat +
    blend/canonicalMean + unit-тесты. [зависит: VI-0 для чисел]

VI-2 Confidence tiers + VoiceAdaptationService: tier-детерминизм,
    async channel, cap/decay/LRU, обновление через существующий
    DurableMemoryStore, аудит adaptive.update. [VI-1]

VI-3 Genesis voice phase: эндпоинты + фразы + quality-гейт + completion
    gate ceremony (voice_enrollment_pending). [VI-1; существующий Genesis flow]

VI-4 Household: 1:N identify (speakerHint) + conversational enrollment
    (интент + owner-гейт + консистентность/distinctness + Users/roles wiring).
    [VI-1, VI-2; существующие enrollment-token/roles]

VI-5 Recovery/diagnostics: endpoints + speakerTier/speakerReason в turn
    response. [VI-1, VI-2]

VI-6 Emotion контракт (interface only, модель отдельно позже). [независимо]

VI-7 Тест-план §12 полный прогон + интеграционные тесты.
```

Что во время Genesis, что после (треб. #12.13):

- **Genesis**: только owner canonical enrollment (5 фраз, качество, ≥4/5).
  Никакой адаптации, никаких других лиц.
- **После**: адаптация подтверждённых HIGH-confidence turn'ов владельца;
  conversational enrollment доверенных лиц (owner-инициированный);
  1:N household identification в каждом turn; diagnostics/recovery.

## 12. Тест-план (треб. #11 — все 20)

| # | Тест пользователя | Где |
|---|---|---|
| 1 | Иван после Genesis → Ivan | VI-3: genesis voice flow unit + integration (enroll→verify≥tier) |
| 2 | Мария после enrollment → Maria | VI-4: conversational enrollment unit |
| 3 | Unknown ≠ Ivan | VI-2: tier LOW никогда не обновляет |
| 4 | Unknown ≠ Maria | VI-2: то же для любого профиля |
| 5 | Иван изменивший тембр → Ivan | VI-1: blend расширяет принятие (unit: близкий вектор к blend) |
| 6 | Громко → identity сохраняется | VI-0: калибровка датасета громких клипов; VI-1 blend unit |
| 7 | Тихо (качество ок) → identity | VI-0/Vi-1: quality-гейт по speechMs, не по громкости |
| 8 | Смех → identity не ломается | VI-0: смех-клипы в калибровке; canonical anchor держит |
| 9 | Злость → identity не ломается | то же |
| 10 | Confidence ниже порога → NO update | VI-2: tier-тесты (0.59 → no update) |
| 11 | Medium → используется, не обучает | VI-2: 0.50 → verify ok, update no |
| 12 | High + quality → update разрешён | VI-2: 0.65+2500ms → candidate |
| 13 | Чужой голос не загрязняет | VI-2: серия чужих HIGH-обманов → bounded blend → reset → pristine |
| 14 | Adaptive reset | VI-5: endpoint unit |
| 15 | Canonical после reset работает | VI-5/VI-1: verify по canonicalMean после reset |
| 16 | Maria ≠ Owner permissions | VI-4: role Family + ceilings (существующие тесты ролей + voice) |
| 17 | Неавторизованный не инициирует enrollment | VI-4: интент без Owner+verified → DENY |
| 18 | Emotion ≠ identity | VI-6: контракт unit — emotion не меняет score |
| 19 | Realtime не блокируется | VI-2: channel bounded, update вне turn-пути (unit: turn без await адаптации) |
| 20 | Перезапуск не теряет профили | VI-1: persistence через DurableMemoryStore (существующий round-trip) |

## 13. Security-чеклист (§28 на компонент)

1. Trusts: существующие IdentityService/аудит; голос — только hint.
2. Invoke: enrollment — Owner (+MFA в Genesis-фазе); adaptive — система.
3. Данные: embeddings (Private scope), raw audio не хранится.
4. Compromised: сброс adaptive; canonical защищён; полная замена — re-enroll
   через security flow.
5. Ревокация: удаление профиля identity → голос больше не идентифицирует.
6. Аудит: enroll/adaptive/reset/diagnose — все события.
7. Fails: speaker service 503 → verification disabled (fail closed для
   адаптации, fail soft для разговора — существующее поведение).
