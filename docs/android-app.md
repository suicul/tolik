# TOLIK Android — Mobile Embodiment & Companion Application (мастер-план, 10.10.2026)

Директива владельца: полноценное Android-приложение — **мобильное воплощение того
же TOLIK**, не отдельный ассистент. Полный спек — в директиве; здесь — решения,
границы, фазы, pre-Genesis граница.

## 1. Главный принцип: ОДИН TOLIK (абсолютный)

```text
                 TOLIK IDENTITY
                       |
                   TOLIK CORE
                       |
              ┌────────┼────────┐
           Windows   Android   Quest 3
           Renderer  Renderer  Renderer
```

- **ЗАПРЕЩЕНО**: независимая личность/память/LLM-агент в приложении, конкурирующие
  с Core. Локальный кэш/настройки/очередь исходящих — НЕ основная память TOLIK.
- Миграция Core (ПК → VPS) — приложение продолжает работать **без переустановки и
  без новой Identity** (конфигурируемый endpoint + безопасное обновление + подтверждение
  нового хоста; домашний IP НИКОГДА не зашит).
- Инварианты (§16): Identity ≠ Device; Authentication ≠ Authorization; Speaker
  Recognition ≠ Permission; Memory/Skill/Emotion ≠ Authorization; Notification ≠ Consent.
  Секреты НИКОГДА в APK; LLM-ключи НИКОГДА в приложении; всё чувствительное
  авторизует Core; мобильный интерфейс не повышает привилегии сам.

## 2. Стек (решение)

- **Kotlin + classic Views (Material 3)** для UI приложения — уже в работе (Foundation
  лейн; меньше тулчейн-зависимостей при отсутствии SDK). Jetpack Compose — будущая
  опция, не текущее решение (директива §19 «рассмотри», не «используй»).
- **Coroutines + Flow**, OkHttp 4 (REST + WS + SSE), Gson (ручные data-классы).
- **Cephalon renderer: custom SurfaceView/TextureView + процедурный 2D Canvas (CPU)** —
  та же философия, что на десктопе (docs/cephalon-desktop.md §3): батарея/GPU-дружелюбно
  (§21: FPS снижается в idle, рендер останавливается когда не видно, качество падает
  при нагреве). НЕТ тяжёлого игрового движка (§6 директивы). Геометрия параметрическая —
  тот же визуальный словарь, что на десктопе (глаз/слои/свечение/частицы), своя
  плотность/детализация под телефон (§3: узнаваемый стиль, не обязана identical).
- CameraX (Phase 5), EncryptedSharedPreferences/Keystore для токенов (§16).

## 3. Cephalon на Android: Expression State — СУЩЕСТВУЮЩИЙ контракт

Директива §4: «используй существующий контракт, не создавай несовместимую модель».

- **TOLIK.Contracts.ExpressionState/ExpressionOutput УЖЕ СУЩЕСТВУЕТ** (commit
  0841765, docs/cephalon-desktop.md §4-§5): typed state (cognitive/emotional subset/
  социальные/голосовые bands/системные — Core ставит SignalLevel) + Composer
  (детерминированный, приоритеты locked>critical>voice>task>emotion>idle,
  параметры НАКАПЛИВАЮТСЯ — веселье+сарказм+речь = ОДНО выражение).
- **ONE COMPOSER, MANY RENDERERS**: Core (State Watcher, CE-2 wiring) компоует и
  отдаёт ExpressionOutput по WS; Android renderer потребляет ТУ ЖЕ wire-модель
  (Kotlin-зеркало JSON-контракта). **Kotlin-порт композера НЕ создаётся** — это
  была бы вторая машина выражений, расходящаяся с десктопом.
- Display modes (§5 директивы): Companion / Fullscreen Presence / Conversation /
  Voice / Compact — typed enum + layout-пресеты; светлые/тёмные системные настройки
  при сохранении палитры Cephalon (синий primary, красный только тревога).
- Pre-Genesis (пока CE-2 wiring не собран): local idle + честный debug («нет
  соединения с Core»), НИКОГДА фейковое состояние (директива §17/§20: визуальное
  состояние отражает реальные данные).

## 4. Модульное разделение (директива §19, адаптировано — без лишних модулей)

```text
TOLIK Android
├── App UI (Activity, modes, Material 3)
├── Cephalon Renderer (SurfaceView + процедурный Canvas, modes)
├── Expression Runtime (Kotlin-зеркало контракта; потребление WS)
├── Voice Interaction (mic capture → Core voice → playback → renderer)
├── Conversation (история, потоковые ответы, отмена, синхронизация)
├── Core Connection (OkHttp REST/WS/SSE, endpoint конфиг, versioning)
├── Device Pairing (bootstrap-паттерн, Keystore)
├── Task Dashboard (create/status/progress/pause/resume/cancel/report)
├── Notifications (категории/приоритеты/тихие часы)
├── Local Cache (кэш/outbox — НЕ основная память)
└── Security / Session (токены, отзыв, повторные ограничения)
```

## 5. Фазы (директива §23, сопоставлено с существующей работой)

```text
AN-P1 Foundation — приложение, сборка, конфигурация Core, безопасное сопряжение,
     существующий API, состояние соединения            [IN PROGRESS (fix-25 лейн)]
AN-P2 Cephalon Mobile — renderer, глаз, процедурная геометрия, Expression State,
     плавные переходы, idle, аудиореактивность          [контракт готов (0841765);
     renderer + modes — следующий лейн; Core-served expression — CE-2 wiring]
AN-P3 Conversation — история, отправка, потоковые ответы, отмена, синхронизация
                                                            [фундамент в fix-25]
AN-P4 Voice — запись, streaming transport, воспроизведение, TTS, прерывание,
     визуальная реакция                                 [фундамент в fix-25;
     полный streaming на живых turn'ах — после лейна]
AN-P5 Camera & Mobile Perception — фотографии по запросу, передача в Core,
     разрешения, индикаторы                             [post-Genesis (AN-3, MM-4);
     СКРЫТАЯ запись/съёмка/обход индикаторов Android ЗАПРЕЩЕНЫ]
AN-P6 Tasks & Notifications — долгоживущие задачи (Core-side: переживают закрытие
     приложения/перезагрузку/миграцию), прогресс, пауза/отмена, push, восстановление
AN-P7 Presence — Presence Manager (IP-4), подтверждаемая передача присутствия,
     подготовка к Quest                                 [post-Genesis (AN-4);
     НИКОГДА не изображать переход до подтверждения целевого устройства]
```

## 6. Сеть / Offline (директивы §17-§18)

- Endpoint конфигурируемый; сопряжение по bootstrap-паттерну; проверка совместимости
  версии API (явная обработка несовместимости — не падать молча); сетевые таймауты +
  ограниченные повторы (никогда бесконечные).
- Offline: последнее подтверждённое состояние + **время обновления** (устаревший
  статус НИКОГДА не выдаётся за актуальный); outbox-очередь с дедупликацией;
  повторная отправка только безопасная; индикатор offline. Независимая личность
  для маскировки сбоя НИКОГДА.

## 7. Уведомления и батарея (директивы §13, §21)

- Категории (завершение задачи/ошибка/нужно решение/восстановление/результат/
  напоминание), приоритеты, тихие часы, настройка частоты. POST_NOTIFICATIONS —
  runtime permission. НИКОГДА постоянный тяжёлый процесс ради соединения; Doze/
  App Standby учитываются; непрерывное прослушивание НЕ обещается (не реализовано
  и не разрешено). Локальная LLM НИКОГДА по умолчанию.

## 8. Pre-Genesis граница

- **Pre-Genesis допустимо**: AN-P1..P4 фундамент (thin, read-only поверх существующего
  Core API — тот же паттерн, что десктопный CE-1/CE-2 core), AN-P2 renderer + modes
  (local idle, честный debug), Kotlin-зеркало контракта.
- **Post-Genesis**: AN-P5 (камера/мобильное зрение — AN-3/MM-4), AN-P7 (Presence —
  AN-4), mind-driven expression (MindState экспорт в host), полный voice-wiring на
  живых turn'ах.
- Genesis НИКОГДА не запускается этим промтом/автоматически (директива §24).

## 9. Тесты (директива §22)

Unit (сейчас): outbox-дедупликация, version-check, mode policy, Expression State
зеркало (нормализация/клэмпинг — без NaN), повторы/таймауты, последний подтверждённый
статус. Device/emulator (после установки SDK владельцем): сопряжение, отзыв,
восстановление соединения, потоковый голос, прерывание TTS, разрешения, lifecycle,
рендер при смене состояния. Инвариант: эмоции/выражения/feedback НИКОГДА не меняют
permissions/Identity/trust/authorization.
