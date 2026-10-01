# TOLIK — доступ с Linux-ноутбука через Tailscale (§28, §37)

Ноутбук — тонкий клиент: вся магия исполняется домашним Windows-ПК
(Windows-агент дергает opencode/Codex/билды локально, результат возвращается).
Tailscale — только транспорт. Авторизация всё равно нужна: Tailscale-сеть
НЕ равна доступу (§37).

## 0. Один файл запуска (дома)

`TOLIK.bat` поднимает всё: стек (postgres/voice/core/+xtts при GPU),
Ollama-модель, bootstrap, Windows-агент **и OpenCode-сервер**
(`opencode serve --port 4096`, своё окно). Флаги отказа:
`-NoAgent -NoBrowser -NoXtts -NoOpenCode`.
Сессии opencode живут на домашнем ПК — с ноутбука продолжается тот же чат.

## 1. Windows-ПК: открыть наружу (только tailnet!)

```powershell
tailscale login            # браузер → войти в свой tailnet
tailscale status           # должен показать 100.x.y.z иPeers
# Если висит "failed to set DNS": tailscale login --accept-dns=false,
# либо выключить MagicDNS в админке tailnet. На авторизацию не влияет.
tailscale serve --bg http://127.0.0.1:5080
# Проверка: https://<имя-пк>.<tailnet>.ts.net/health  → 200 (TLS от Tailscale)

## 1b. OpenCode наружу (тот же механизм)

```powershell
# Второй backend рядом с TOLIK (свой HTTPS-порт или path — см. tailscale serve --help):
tailscale serve --bg --https=8443 http://127.0.0.1:4096
# С ноутбука — два варианта одного чата (сессии серверные, продолжаются):
opencode attach https://<имя-пк>.<tailnet>.ts.net:8443   # TUI (нужен opencode на ноуте)
# или открыть тот же URL в браузере — serve раздаёт и web UI, и API
```
Если порт/путь не заведётся с первого раза — `tailscale serve --help` и
`tailscale serve status` покажут точный синтаксис твоей версии клиента.
```

НЕ использовать `tailscale funnel` — он выставляет наружу в интернет, нужен
только tailnet (`serve`). Core по-прежнему слушает только loopback.

## 2. Linux-ноутбук (работа)

```bash
curl -fsSL https://tailscale.com/install.sh | sh
sudo tailscale login     # тот же аккаунт
curl -s https://<имя-пк>.<tailnet>.ts.net/health   # 200

cp tolik-linux.sh ~/bin/tolik && chmod +x ~/bin/tolik
export TOLIK_ENDPOINT=https://<имя-пк>.<tailnet>.ts.net
export TOLIK_BOOTSTRAP=<код из домашнего .env, одноразово показать>
./tolik-linux.sh --enroll   # P-256 ключ + свой device + свой токен (600)
```

`--enroll` нужен один раз. Ключ устройства (`~/.tolik/device.key`) не покидает
ноутбук; bootstrap-код после enroll можно ротировать.

## 3. Использование

```bash
export TOLIK_ENDPOINT=https://<имя-пк>.<tailnet>.ts.net
export TOLIK_TOKEN=$(cat ~/.tolik/session-token)
tolik "разберись почему не собирается проект"         # чат
tolik --task "run opencode for project X"             # opencode исполнится ДОМА
tolik --agents                                        # агент онлайн?
tolik --remind "позвонить" --text "…" --delay 3600
```

Путь `run_opencode`/`run_codex`/`run_build` через `/tasks` — штатный:
Windows-агент выполняет, Core возвращает результат. Всё audited.

## 4. Запасной путь — SSH

Если Tailscale недоступен: OpenSSH Server на Windows
(Settings → Apps → Optional features → OpenSSH Server, `Start-Service sshd`),
с работы: `ssh user@<windows-ip> "C:\DEV\TOLIK_QWEN\dist\cli\TOLIK.Cli.exe chat ..."`.
Ключи вместо паролей; это вне периметра TOLIK-аудита.

## 5. Безопасность

- Потерян ноутбук → revoke device (`POST /auth/devices/{id}/revoke` с дома),
  токен на нём умирает; файлы `.tolik/` — `chmod 600` (скрипт делает сам).
- Bootstrap-код не светить в чатах/логах; после enroll всех устройств — ротировать.
- Destructive/critical с ноутбука — те же confirmations/MFA, голос владельца
  не требуется, но и не даёт лишнего: роль Family scoped (см. план).
- Tailscale ACL: по умолчанию устройства видят друг друга; при желании закрыть
  в админке до «ноутбук → только домашний ПК:443».
