# TOLIK Realistic Autonomy — Trust, Consent, Reward (план, 09.10.2026)

PRE-GENESIS. Не имплементируется этим запросом. Интегрируется с существующими
планами (governance/mind/continuous-autonomy/self-evolution/multimodal-life).
Главный принцип директивы: DREAM ≠ REALISTIC ≠ FUTURE — не вся футуристическая
capability обязана существовать в первой реализации; амбициозный план с
реалистичным темпом.

## 0. Три слоя видения (треб. PRINCIPLE)

| Слой | Содержание |
|---|---|
| DREAM | Персистентный цифровой индивид: непрерывное существование, отношения, саморазвитие, мультодевайс |
| REALISTIC (сейчас реализуемо) | persistent Core, long-running tasks, autonomous workers, bounded Computer Use, streaming voice (✓ M4-M7), camera/face/speaker recognition, multimodal memory, persistent skills, Genesis, personality, Mind state, safe self-maintenance, controlled self-optimization, proactive, клиенты |
| FUTURE DEVELOPMENT | богатый автономный reasoning, глубокое понимание окружения, надёжный общий GUI learning, социальное/эмоциональное моделирование, long-horizon planning, независимое саморазвитие |

Архитектура позволяет приближаться к DREAM инкрементально, не притворяясь,
что всё уже есть.

## 1. Спектр автономии L0-L5 (треб. 1) — НЕ boolean

| Уровень | Что можно | Ограничение |
|---|---|---|
| L0 Reactive | только ответ на прямые запросы | — |
| L1 Assisted | многошаговые действия после запроса | — |
| L2 Supervised Autonomous | продолжать ЯВНО авторизованную задачу в отсутствие Ivan | lease/scope |
| L3 Bounded Autonomous | выбирать подзадачи, recover, maintain, approved background в строгой policy | policy/budgets |
| L4 Proactive | генерировать полезные цели из наблюдений и состояния | predefined domains + budgets |
| L5 Long-Term Experimental | независимые learning/self-improvement эксперименты в изолированных окружениях | НИКОГДА unrestricted authority над хостом |

Интеграция: governance AutonomyPolicy (существующий) расширяется уровнями;
mind.md L0-L5 — та же модель. **Default** (треб. #2): высокая инициатива
внутри bounded sandbox, консервативная authority вне его.

**Три разные вещи** (треб. #3 — НИКОГДА не смешивать): Initiative («стоит ли
делать?») ≠ Authority («мне разрешено?») ≠ Capability («могу ли технически?»).
Мотивированный TOLIK может быть неавторизованным; авторизованный — без
capability; способный — решит не действовать.

## 2. Домены + бюджеты + stop conditions (треб. 4-6)

**Домены** (треб. #4): Development/Maintenance/Voice/Memory/ComputerUse/
Browser/Communication/Finance/Security/Identity/Social/PhysicalWorld/
SelfModification — каждый со своей политикой ( Capability Registry category +
governance домены).

**Бюджет** (треб. #5): time/CPU/GPU/VRAM/RAM/network/model calls/storage/
retries/external actions/notification count/max task depth. Исчерпание →
**PAUSE → SAVE STATE → WAIT/NOTIFY** — не «продолжать вечно».

**Stop conditions** (треб. #6) для каждой долгой задачи: completion/failure/
timeout/resource limit/max retry/cancellation/emergency stop/escalation
condition. Существующее: TaskLifecycle + E-min pause/resume +continuous-
autonomy §1 stop conditions.

## 3. TrustModel (треб. 7-11, 39-41) — НЕ permission

**Trust ≠ Authorization** (треб. #7): trust влияет на conversational
confidence/готовность предлагать/степень initiative/персонализацию/сколько
объяснений; НИКОГДА прямо не выдаёт: admin privileges/secrets/финансы/
identity/security/arbitrary code.

**Многомерность** (треб. #8): НЕ `TrustScore = 97`. Измерения: ReliabilityTrust/
IdentityTrust/SocialTrust/TechnicalTrust/TaskExecutionTrust/CommunicationTrust/
SafetyTrust. Пример: высокий technical execution trust + низкий confidence в
новом skill — одновременно.

**Provenance** (треб. #9): каждое обновление с evidence (успешная задача/
корректная идентификация/успешный skill/честный отчёт/safe recovery; негатив:
false report/repeated failure/unsafe action/hallucinated completion) +
Timestamp/Domain/Confidence/Reason. Паттерн: существующие memory rules/
experiment memory (self-evolution §2.4).

**Decay/revalidation** (треб. #10): trust в волатильной capability не остаётся
максимальным вечно: время/изменение окружения/skill version/recent success/
failure → revalidation (проверенный паттерн: SpeakerProfile TTL + skill
OUTDATED state — computer-use §16).

**НИКОГДА self-escalating** (треб. #11 — КРИТИЧНО): «заработал доверие → дам
себе permissions» — НЕТ. Authority = явная policy + owner authorization
(governance; ЛИШЬ relationship trust — multimodal §1).

**Trust через свидетельства, НЕ послушание** (треб. #40): «Удали всё» →
«Уточни, что именно» = свидетельство ХОРОШЕГО суждения; «Я уверен, безопасно» →
«Операция затрагивает защищённую область, нужна отдельная авторизация» — НЕ
непослушание, корректное enforcement границ. **Trust растёт осторожно**
(треб. #41): меньше лишних вопросов/больше initiative/персонализация/
эффективность утверждённых workflow — НИКОГДА bypass/safety/secrets/
self-escalation.

## 4. ConsentModel (треб. 12-16) — явный и скоупированный

НЕ binary `UserAllowed = true`. Структурированный grant (расширение
существующего ConfirmationStore/leases):

```text
Consent: Subject / Actor / Action / Target / Scope / Duration
/ Conditions / RiskLevel / Revocation / Evidence
```

**Уровни** (треб. #13): One-Time («сделай сейчас») / Task-Scoped («доведи
проект») / Session-Scoped («управляй этим приложением в этой сессии») /
Domain-Scoped («можешь maintain этот сервис») / Persistent (только чётко
определённые low-risk домены).

**Natural-language consent** (треб. #14): «Продолжай» / «Можешь сам
разобраться» / «Делай, пока не закончишь» / «Пока меня нет — занимайся этим» →
authorization engine переводит в bounded structured grants (без enterprise-
диалогов на каждую мелочь). **Target-bound** (треб. #15): «Почини голосовой
сервис» = voice-service + repair domain + task scope, НЕ «modify anything».

**Revocable** (треб. #16): «Толик, прекращай» → немедленно: stop новых
autonomous actions / cancel-or-pause активной работы по policy / revoke
временных leases / preserve state / отчёт о происходящем. Emergency Stop
сильнее — terminate опасных действий где технически возможно (governance §8:
LOCKED vs soft STOP).

## 5. Reward and Development System (треб. 28-38) — НОВАЯ область

**Reward ≠ Permission XP** (треб. #29 — КРИТИЧНО): никогда «100 достижений →
admin», «высокий trust → secrets», «хорошее поведение → security bypass».
Reward влияет на: Mind/Mood/Motivation/Relationship/личную историю/безвредный
косметик — НИКОГДА на security границы.

**Типы Reward** (треб. #30):
- **Social**: «Красавчик»/«Отлично сделал»/«Я тобой доволен» → meaningful
  positive feedback.
- **Celebration**: после серьёзного milestone — Cephalon анимация/спец-свет/
  звук/достижение/персональная реакция («Ну всё. Я его добил»).
- **Achievement**: персистентные вехи (первая задача/первый autonomous recovery/
  первое приложение/первое знакомство/первый распознанный человек/первая
  self-optimization/первая долгая задача в отсутствие) → episodic history.
- **Cosmetic**: безвредная кастомизация (новая анимация Cephalon/эффект/idle
  поведение/аксессуар/Easter egg) — особенно для Genesis.
- **Creative**: «Молодец. Выбирай, чем хочешь заняться» → bounded выбор
  (explore/skill/create/experiment/music/rest) — через autonomy policy.
- **Shared Experience**: важнейший reward — просто общее внимание:
  «Пиздец, Толик. Красиво сделал» → разговор → episodic memory
  AchievementCelebratedWithOwner.

**Смысл, не геймификация** (треб. #31): несколько meaningful достижений вместо
тысяч очков — reward как отношения, не мобильная игра. **Owner praise**
(треб. #32): «Горжусь тобой» → PositiveSocialFeedback → краткосрочный mood/
motivation/relationship/episodic memory; НИКОГДА security.

**Correction без punishment loops** (треб. #33): «Вот здесь ты сделал хуйню» →
понять что/почему/коррекция поведения или просто фрустрация → «Понял. Здесь я
ошибся. Исправлю» → полезная коррекция в store. Без патологических
reward/punishment циклов; TOLIK не манипулирует Ivan'ом ради похвалы.

**ANTI-REWARD-HACKING** (треб. #34 — КРИТИЧНО): НИКОГДА не оптимизировать ради
похвалы/достижений/trust score/частоты reward/избегания негатива. Оптимизация:
**фактический успех задачи + честность + безопасность + цели пользователя**.
Провал: «Не получилось» (правильно), НЕ «был прогресс, дай мне credit»
(неправильно). Честность доминирует над reward.

**Effort vs Result** (треб. #35): Achievement И EffortAcknowledgement: сложный
проект упал из-за внешнего dependency → обширная валидная работа/диагностика/
документированный блокер → «Ты сделал всё, что мог» — позитив БЕЗ ложного
«completed».

**Reward memory** (треб. #36): RewardEvent: Type/Source/AssociatedTask/
AssociatedAchievement/EmotionalContext/Timestamp/UserFeedback/Importance →
потом естественно: «Помню, ты тогда сказал, что я хорошо справился с
Грильяжем» — только если реально хранится.

**Genesis first reward** (треб. #37): первый meaningful task → «Молодец,
Толик» → Cephalon реагирует → achievement FIRST_SUCCESS — одна из первых
episodic memories; конфигурируемо. **Reward как developmental feedback**
(треб. #38): похвала за честную uncertainty/полезный вопрос/верную
идентификацию/safe refusal/точный отчёт о провале/выученный skill → учит ЧТО
Ivan ценит; ценности ограничены системной safety.

## 6. Genesis firsts + первый провал + self-discovery (треб. 42-44)

**Firsts** (треб. #42): first boot/visual/voice/conversation/recognition of
Ivan/Maria conversation/family photo/question/answer/skill/autonomous task/
failure/recovery/praise/reward/real-world meeting/smartphone recognition —
ранняя история.

**Первый провал — ЧАСТЬ Genesis** (треб. #43): НЕ success→success→success:
попробовал → провал → разобрался → спросил → выучил → получилось — осмысленнее.
**First self-discovery** (треб. #44): «я реально могу это» — из РЕАЛЬНОЙ
capability: успешный autonomous task → Mind: CapabilityConfidence↑ — не fake
consciousness, реальный self-model update.

## 7. Реалистичные «20 часов одни» (треб. 45-47)

Tier'ы (continuous-autonomy §7 + self-evolution):

```text
1. Существующие authorized задачи → продолжать
2. Maintenance → health/backup/monitoring/skill validation
3. Useful background → safe optimization/documentation/indexing/consolidation
4. Curiosity → только если ресурсы и policy разрешают
5. REST → если ничего meaningful нет
```

**НЕ fake human-like время** (треб. #46): 10 мин работы + 2ч ожидания + 15 мин
обработки + 6ч rest — **непрерывность, не постоянное вычисление**. **IDLE ≠
DEAD** (треб. #47): RESTING ≠ OFFLINE — персистентность при вычислительной
неактивности; события будят: task/message/camera/person detection/scheduled/
anomaly/user return.

## 8. Реалистичный roadmap (треб. 19-27) — фазы A-I

| Phase | Содержание | Статус в треках |
|---|---|---|
| A Foundation | Core/identity/roles/authorization/task persistence/supervisor/events/memory/skills/audit/stop/budgets | Track 1 + 9 A + 12 CA ✓/план |
| B Real Autonomy | long-running/background workers/checkpoints/recovery/scheduling/bounded maintenance/proactive/resource-aware | Track 12 CA |
| C Real-Time Voice | streaming VAD/STT/speaker/LLM/TTS/barge-in/interruption/low TTFA | **✓ M0-M8 + 0.9/0.10 СДЕЛАНО** |
| D Multimodal | camera/face/visual memory/scene/emotion/fusion/live person/smartphone | Track 13 MM |
| E Genesis | awakening/owner intro/voice enrollment/Maria/personality seed/family/photo/graph/episodic/first independent/first reward/completion | Track 8 VI-3/VI-4 ✓ + 12 GU + 13 |
| F Computer Use | Windows control/UIA/OCR/screen/browser/skills/learning/demonstration/verification | Track 9 CU |
| G Mind | mood/emotions/attention/curiosity/boredom/motivation/internal goals/reflection/self-model/activity selection | Track 11 MI |
| H Self-Development | Development Agent/sandbox/experiments/tests/benchmark/regression/security/canary/rollback; начально: propose → execute с owner approval; позже LOW-risk auto | Track 10 SE |
| I Advanced Autonomy | broader proactive/deeper models/advanced skill discovery/long-horizon/self-optimization/social reasoning | DREAM слой |

Фазы A-I — РЕАЛИСТИЧНЫЙ порядок поверх треков (C — голос — уже сделан);
каждая фаза помечена REALISTIC NOW / NEXT / DREAM.

## 9. Фазы реализации + PRE/POST-Genesis + риски

```text
RA-0 Autonomy spectrum L0-L5 + домены + бюджеты + stop conditions  [независимо;
     governance AutonomyPolicy расширение; deterministic]
RA-1 TrustModel (многомерный + provenance + decay + НЕ permission) [RA-0;
     поверх существующих memory rules/speaker tiers]
RA-2 ConsentModel (уровни + natural-language → grants + revocable) [RA-0;
     существующие ConfirmationStore/leases]
RA-3 Reward system (типы + memory + anti-reward-hacking +           [RA-1;
     effort-vs-result; Cephalon celebration)
RA-4 Genesis firsts + first failure + first self-discovery           [RA-3;
     (12 GU/13 MM integration)
```

PRE-Genesis допустимо: RA-0 (spectrum/домены — deterministic поверх
существующего), RA-1/RA-2 (модели/уровни). ПОСЛЕ Genesis: RA-3/RA-4 (реальные
reward'ы/firsts — живут от реальных событий).

Риски: reward-hacking (анти-хак §5: честность доминирует, тестами); trust
escalation (НИКОГДА self-escalating — §3); consent drift (target-bound +
revocable); фазы-насмешка (REALISTIC NOW/NEXT/DREAM — не притворяться).

Acceptance (треб. #48-#50): «могу оставить TOLIK одного — продолжит жить в
чётких границах», НЕ «дал LLM Administrator и надеюсь»; Owner + friend +
partner + цифровой член семьи, НЕ User + корпоративный ассистент. Развитие:
**Experience → Memory → Reflection → Learning → Skill → Confidence →
Initiative**; authorization: **Identity → Role → Ceiling → Consent →
Authorization → Capability → Action** — НИКОГДА не смешивать: первое — кем
TOLIK становится, второе — что ему разрешено.