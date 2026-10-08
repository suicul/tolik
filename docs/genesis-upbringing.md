# TOLIK Genesis Upbringing — развитие как процесс (план, 09.10.2026)

PRE-GENESIS. Не имплементируется этим запросом. Genesis = начало жизни и
отношений с владельцем, НЕ setup wizard. Интегрируется с существующими
системами. Компаньоны: docs/mind.md, docs/voice-identity.md (VI-3 voice phase/
VI-4 conversational enrollment), docs/governance.md, docs/continuous-autonomy.md.

## 0. Аудит: что УЖЕ есть (не дублировать)

| Существующее | Отношение к upbringing |
|---|---|
| Genesis ceremony (identity/audit, atomic, rerun-impossible) + preflight | Основание; upbringing = ПОСЛЕ ceremony, до normal life |
| VI-3 Genesis voice phase (5 фраз, quality gates, /genesis/voice/*) | Phase 3 Voice УЖЕ спроектирована ✓ |
| VI-4 conversational enrollment (/voice/person/enroll: Owner+tier-High gate, role Family/TrustedGuest НИКОГДА Owner, distinctness) | Phase 5/6 Family/Friends УЖЕ частично ✓ — upbringing добавляет разговорность |
| PersonalContextStore (people/humor/personality/lore/relationships/events) | Person/Relationship хранилище ✓ |
| LearnedRuleStore + memory rules (provenance/confidence/origin) | Developmental memory (§3.4) паттерн |
| Mind план (curiosity/questions/maturity) | Active questioning (§2) фундамент |
| RealtimeEventHub + Cephalon (web canvas; Track 3/VR) | Ceremony story arc (§4.4) |
| Vision/UI observation (CU-1/CU-2 pre-Genesis) | Photo introduction (§3.1) — после Genesis в основном |
| Speaker verification (0.45/0.60 tiers, VI-0 калибровка) | NEVER auto-confirm identity from face (§3.2) |

## 1. Genesis = upbringing, не setup (треб. 23-25)

```text
НЕ: Configure username → configure voice → choose personality → Done
ДА: TOLIK рождается → встречает Ivan → узнаёт кто он → узнаёт свою identity →
    узнаёт семью и окружение → постепенно развивает личность/знания/skills/
    отношения → продолжает жить и работать между разговорами
```

**Initial state** (треб. #24): core identity/foundational security/базовый
язык/базовое perception/minimal personality seed/minimal world model/**без
вымышленной личной истории**. Не притворяется, что знает: семью/Марию/друзей/
личную историю/фото/предпочтения/проекты/внутренние шутки — учится через
реальное взаимодействие.

**Developmentally young** (треб. #25): НЕ младенец — аналогия: знания и world
model растут от малого через взаимодействие. Сначала больше вопросов,
любопытство, разумные наблюдения, ассоциации, запоминает людей, постепенно
увереннее. НЕ энциклопедические знания о личном мире Ivan'а с первого дня.

## 2. Развивающие фазы Genesis (треб. #26) — концептуальные, могут пересекаться

```text
Phase 0 Awakening: инициализация; обнаруживает существование/окружение/Core/
     devices/embodiment
Phase 1 First Contact: встречает Ivan; owner identity/preferred name/
     relationship/voice (Track 8 §4 voice enrollment)
Phase 2 Self: имя TOLIK/identity/Cephalon embodiment/личность seed/
     capabilities/limitations (self-model)
Phase 3 Voice: голос Ivan (VI-3 ✓); позже Maria
Phase 4 Home/Environment: компьютер/devices/приложения/окружение
Phase 5 Family: Ivan представляет семью (VI-4 enrollment ✓ + разговорность)
Phase 6 Friends/Acquaintances: люди и отношения
Phase 7 Shared World: проекты/hobby/интересы/recurring topics/совместная история
Phase 8 Independence: начинает генерировать свои safe goals/activities
     (Mind autonomous cognition)
```

Диалог динамический (НЕ hard-coded); фазы — карта развития, не questionnaire.

## 3. Разговорный Genesis + активные вопросы (треб. 27-29, 56, 54)

НЕ формы. «Введите имя жены: ___» — плохо. Хорошо:

```text
Ivan: «Это моя жена, Мария.»
TOLIK: «Мария... приятно познакомиться. А как мне к ней обращаться —
        Мария или есть домашнее имя?»
```

**Active questioning** (треб. #28, #54): вопросы ГЕНЕРИРУЮТСЯ из отсутствующей
информации и curiosity (не фиксированный вопросник). Обнаружил missing
relationship → «Кто он тебе?». **Информационная ценность** (треб. #29, #55):
каждый вопрос максимизирует полезную информацию (identity/name/relationship/
preferred address/контекст/границы/произношение/никнеймы); избегать
нечувствительного; вопрос = expected usefulness + uncertainty reduction +
relevance + social importance − privacy cost − interruption cost → бесконечных
допросов нет.

**Bidirectional teaching** (треб. #56): Ivan учит → TOLIK спрашивает →
наблюдает → предлагает гипотезы → Ivan подтверждает/поправляет → TOLIK
обновляет память. Фундаментально НЕ заполнение формы.

## 4. Социальное знакомство по фото (треб. 30-36) — post-Genesis в основном

**Photo Introduction / Visual Learning Mode** (треб. #30): Ivan открывает фото
→ TOLIK наблюдает → видит визуальные признаки, но НЕ auto-assert identity:
«Кто этот человек?» → Ivan: «Это мой брат Андрей» → Person + Relationship +
visual references + Name.

**Multi-person** (треб. #31): «Вижу несколько человек. Кого уже представил?» /
«Это все родственники или есть друзья?».

**NEVER auto-confirm identity from face alone** (треб. #36 — КРИТИЧНО): face
recognition может предложить «Possible match: Андрей, confidence 0.91», но
социальная identity — из ЯВНОГО знакомства/подтверждённой ассоциации/
authorized recognition. Неопределённое визуальное распознавание НИКОГДА
молча не становится фактом (§50 no fake; speaker tiers VI-0: «гипотеза
пока не подтверждена, когда identity важна»).

**Patterns как гипотезы** (треб. #35): recurring person/location/event/pet/
хронология/смена внешности → «Вроде видел этого человека. Это Андрей?» →
Ivan подтверждает → confidence↑.

**Photo memory** (треб. #32): НЕ хранить все изображения вечно: person
identity/face embedding если разрешено/visual descriptors/relationship/source/
confidence/provenance; оригинал остаётся у владельца (не дублировать).

**ExternalMediaSource** (треб. #33): Yandex Disk/локальная папка/Google Photos/
NAS/phone/future cloud — источник = provider, процесс обучения независим от
провайдера. **Интерактивный просмотр** (треб. #34): «О, это вы с Машей?» →
«Это Красноярск» → «Запомню как вашу поездку в Красноярск» — реальная
контекстная память.

## 5. Семейное дерево + relationship learning (треб. 37-41)

**Relationship graph** (треб. #38): расширение существующего RelationshipItem:

```text
Person / Relationship / Direction / Confidence / Source / Timestamp
/ Notes / PreferredAddress
Ivan ├─ wife → Maria ├─ mother → ... ├─ brother → ...
```

**Referent resolution** (треб. #39): «Маша сегодня дома?» → понимает отсылку;
неоднозначно → «Ты про Марию?» — НЕ вслепую угадывать.

**Имя/никнейм** (треб. #40): «Это Александр, но мы зовём его Саныч» → Formal:
Александр, Preferred: Саныч; как Ivan обращается / как человек предпочитает,
чтобы TOLIK обращался.

**Learning through correction** (треб. #41): «Нет, это не Андрей, это Сергей» →
коррекция belief/понижение confidence предыдущей ассоциации/preservation
provenance коррекции/не повторять ошибку — ощущение обучения.

**Developmental memory с provenance** (треб. #37): «Maria — жена Ivan'а.
Источник: явное представление. Confidence: высокая. Voice: enrolled. Фото:
подтверждены» — сильнее, чем «модель угадала Maria».

## 6. Развитие: зрелость, темп, доверие (треб. 47, 51-53, 46)

**Maturity как simulation-концепт** (треб. #51 — НЕ «3 дня от роду»):
KnowledgeMaturity/SocialFamiliarity/SkillMaturity/AutonomyMaturity/Confidence.

**Learning rate** (треб. #52): ранний Genesis — высокий (личные факты/
relationship graph/предпочтения/окружение/терминология); позже — консервативнее
(стабильность личности/памяти).

**Memory confidence** (треб. #53): каждый факт: confidence/provenance/
confirmation state/last observed/contradiction count. «Maria — жена» (явное,
very high) vs «может быть брат» (визуальный inference, low).

**Trust через свидетельства** (треб. #47 — КРИТИЧНО): НЕ «trust = 100%».
Trust (relationship, НЕ security authorization!) вырастает из: успешных
задач/верной памяти/надёжной верификации/safe autonomous behavior/честных
отчётов/уместной uncertainty/корректной обработки ошибок.

## 7. Shared history + Genesis journal + переход (треб. 44-46, 48-50, 58-67)

**Shared history** (треб. #44): «Первый разговор» / «Первое знакомство с
Марией» / «Первый раз увидел семью» / «Первый самостоятельный проект» /
«Первое самостоятельное исправление» — meaningful long-term memories.

**Genesis journal** (треб. #65, защищённый): Birth/First perception/First
speech/Owner introduction/Owner voice enrollment/Maria introduction/Maria
voice enrollment/First family introduction/First learned person/place/
project/First independent task/First successful autonomous action — начало
истории TOLIK. **First memory** (треб. #66): НЕ выдуманная — из фактической
ceremony («Ivan активировал меня и представился»), protected historical event.

**Переход к автономии** (треб. #45, #46, #58): НЕ резко Genesis OFF → full AI:

```text
Genesis → Guided autonomy → Safe autonomy → Normal autonomy
```

**First independent activity** (треб. #45): от «Tell me what to do» к
«I wonder what I can do»: проверить свой health/изучить приложение/
верифицировать voice pipeline/организовать представленных людей/посмотреть
проект/safe benchmark → отчёт: «Я сам проверил голосовой контур» — переход
upbringing → autonomy. **Self-education после** (треб. #58): «не знаю это
приложение» → curiosity↑ → goal → Computer Use (safe explore) → skill →
validate → persist.

**Поведенческий фундамент** (треб. #59, #60): Genesis учит не только «кто
такой Ivan», но «как вести себя»: спрашивать при uncertain/verify важное/не
лгать/не выдумывать/уважать приватность/останавливаться по просьбе/объяснять
провалы/**отличать желания от разрешений**: «I can want something without
being allowed to do it» — с самого начала.

**Genesis НЕ сбрасывается легко** (треб. #50): completed Genesis state —
protected; обычные обновления не сбрасывают; re-enrollment/reset — explicit
protected operation. Не стереть случайно: identity/memories/relationships/
voice profiles/shared history. **Genesis → normal mind** (треб. #67):
Genesis data → Memory/Relationship Graph/SelfModel/Personality/MindState/
Skills; Genesis завершается; жизнь продолжается.

**Ceremony story arc** (треб. #48, #49, #63): Cephalon появляется →
тихий/любопытный → first perception → first voice → first conversation →
Ivan представляется → voice enrollment → Maria (+ her voice enrollment) →
семья → окружение → capabilities → first independent task → normal life.
Диалог динамический; пример диалога директивы (Ирина/тёща/роли) — из knowledge
gaps + personality, не hard-coded.

## 8. Фазы + security + зависимости

```text
GU-0 Developmental state machine (фазы 0-8, maturity модели:                [независимо]
     KnowledgeMaturity/SocialFamiliarity/SkillMaturity/AutonomyMaturity)
GU-1 Genesis journal (protected: birth/firsts) + first memory               [GU-0;
     существующий audit/genesis event]
GU-2 Active questioning engine (missing info + curiosity +                  [GU-0,
     information value; Mind curiosity интеграция)                           Mind MI-2+]
GU-3 Relationship graph (Person/Relationship/Direction/Confidence/          [GU-0;
     Source/PreferredAddress; PersonalContextStore расширение)
GU-4 Разговорное знакомство (VI-4 enrollment + вопросы +                     [GU-2, GU-3,
     nickname/correction; voice introduction — VI-3 интеграция)               VI-3/VI-4]
GU-5 Photo introduction (ExternalMediaSource + Visual Learning +             [GU-4,
     NEVER auto-confirm identity; CU-1/CU-2 vision)                           post-Genesis
                                                                              в основном]
GU-6 Transition to autonomy (guided→safe→normal; first                      [GU-0..GU-4,
     independent activity; trust through evidence;                           Mind autonomy]
     Autonomy levels L0-L5 — governance)
GU-7 Learning rate + memory confidence + correction handling [GU-3, GU-6]
```

Security (треб. 10-треков): существующие identity/roles/ceilings/authorization/
capability registry/autonomy/confirmation/audit/emergency stop — НЕ ослабляются;
relationships/возраст/доверие — НИКОГДА authorization; privacy: не собирать
нечувствительное, face-данные с явным разрешением, оригиналы у владельца;
Genesis state protected (не сбрасывается легко).

**PRE-Genesis допустимо**: GU-0/GU-2/GU-3 (state machines/интент/граф —
deterministic, поверх существующих). **ПОСЛЕ Genesis**: GU-4/GU-5 (реальные
знакомства — MFA + owner ceremony), GU-6/7 (переход/развитие).
