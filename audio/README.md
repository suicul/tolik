# TOLIK Soundboard

Реакции и мемы, которые TOLIK может проигрывать на ПК через Агента (AGENTS.md §17).

```
audio/
├── reactions/   # короткие реакции: "wow", "бум", "аплодисменты"
├── memes/       # любимые мемы
├── family/      # семейные звуки и шутки
└── system/      # build done, reminder chime, alert
```

## Использование

```
POST /audio/play   { "category": "reactions", "name": "wow.mp3" }
GET  /audio/library
```

Файлы кладутся в `TOLIK_AUDIO_ROOT/<category>/` на машине, где работает **TOLIK.Agent**
(по умолчанию — рядом с бинарником Агента, задать через переменную `TOLIK_AUDIO_ROOT`).

Ограничения (§17):
- только wav/mp3, до 20 МБ;
- имя — без путей (`..`, `/`, `\` запрещены);
- rate limit: один звук на пользователя в 10 секунд (HTTP 429 при превышении);
- запуск — только через capability PlaySound у подключённого Агента.
