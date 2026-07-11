# Тестирование AmneziaGeo (Windows) — отладочный прогон

Консольный хост `AmneziaGeo.Windows.App` — это набор подкоманд. Документ описывает ручную
проверку слоёв до появления UI.

## Требования

- .NET 10 SDK, собранный проект (Debug).
- **Запуск от администратора** для команд, создающих службы/туннели/маршруты
  (`install` / `start` / `stop` / `uninstall` / `profile-run` / `agent-*`). Команды настроек,
  конфигов и гео админа не требуют.
- Состояние пишется в `C:\ProgramData\AmneziaGeo\state.db` (SYSTEM/админ-контекст).
  Неэлевированный процесс упрётся в `SQLite Error 8: attempt to write a readonly database` —
  это не баг кода, а права на файл.

## Быстрый запуск

В сессии PowerShell заведи обёртку (живёт до закрытия окна; функция привязана к **этой** сессии):

```powershell
function ageo { & "C:\dev\tools\amnezia\amneziageo\amneziageo-windows\AmneziaGeo.Windows.App\bin\Debug\net10.0\AmneziaGeo.Windows.App.exe" @args }
```

Либо в Visual Studio — профили в `AmneziaGeo.Windows.App\Properties\launchSettings.json`
(переключаются дропдауном у кнопки Start):

```json
{
  "profiles": {
    "settings":     { "commandName": "Project", "commandLineArgs": "settings" },
    "config-list":  { "commandName": "Project", "commandLineArgs": "config-list" },
    "geo-query cn": { "commandName": "Project", "commandLineArgs": "geo-query ip cn" }
  }
}
```

Без аргументов приложение запускает демо (генерит ключ, пишет профиль `default`) — это
проверка, что БД и P/Invoke-движок живы.

## Готовые скрипты (быстрая проверка)

В `amneziageo-windows/scripts/` — набор для запуска в **админском** PowerShell:

- `vpn-on.ps1` — full-tunnel: ставит `proba`, поднимает, печатает egress before/after. DNS
  приложение выставляет само → в браузере работает любой сайт. Безопасно **только за физической/Hyper-V
  консолью** (full-tunnel включает kill-switch и обрубает RDP).
- `vpn-off.ps1` — останавливает и удаляет `proba`; DNS откатывается автоматически.
- `vpn-geo.ps1 [-Site 2ip.ru]` — geo-split: через VPN идёт только указанный сайт (в браузере
  отключи Secure DNS/DoH, иначе перехват DNS обходится).
- `agent-test.ps1 [-Stop]` — ставит и запускает агент-службу для одиночного конфига `proba`
  (full-tunnel, группа из 1), печатает статус/лог/egress; `-Stop` гасит и удаляет агент. Тоже
  **только за консолью**.
- `cleanup.ps1` — убирает агент, тестовые конфиги/службы, балансировщики, дубли гео-источников,
  сбрасывает DNS.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File amneziageo-windows\scripts\vpn-on.ps1
# открой сайт в браузере ...
powershell -NoProfile -ExecutionPolicy Bypass -File amneziageo-windows\scripts\vpn-off.ps1
```

## Ступень 1 — настройки и БД (без админа)

```powershell
ageo settings
ageo set-option refresh-seconds 45
ageo settings              # значение сохранилось => запись в БД работает
ageo set-option refresh-seconds 60
```

## Ступень 2 — CRUD конфигов (без админа)

```powershell
ageo config-add proba "C:\dev\tools\amnezia\amneziageo\.claude\tunnel-template.conf"
ageo config-list           # имя, endpoint, состояние службы (ABSENT = служба не создана)
ageo config-show proba
ageo config-copy proba proba2
ageo config-remove proba2
```

На стенде тестовый конфиг — `.claude\tunnel-template.conf` (gitignored, содержит реальный
приватный ключ; в репозиторий не коммитится).

## Ступень 3 — гео-конвейер (нужна сеть, без админа)

```powershell
ageo add-source geoip   https://github.com/Loyalsoldier/geoip/releases/latest/download/geoip.dat
ageo add-source geosite https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/geosite.dat
ageo list-sources
ageo update-sources        # качает .dat, парсит, материализует активный набор
ageo geo-query ip cn       # число CIDR для страны
ageo geo-query site openai # домены категории
```

## Ступень 4 — живой туннель (АДМИН; осторожно с full-tunnel)

> ⚠️ Шаблон — **full-tunnel** (`AllowedIPs = 0.0.0.0/0`). Если работаешь по **RDP/SSH**,
> full-tunnel включит kill-switch и **обрубит твою сессию**. Сужай маршруты через geo-split.

`install` создаёт службу Windows и кладёт конфиг. `config-add` (ступень 2) только регистрирует
конфиг — службы не создаёт, поэтому `start` после одного лишь `config-add` даст
`1060: service does not exist`. Перед `start` всегда нужен `install`.

```powershell
ageo install proba "C:\dev\tools\amnezia\amneziageo\.claude\tunnel-template.conf"
ageo set-geo proba on cidr:10.0.0.0/24   # в туннель только VPN-подсеть => без kill-switch
ageo start proba
ageo status proba                         # STATE ... RUNNING
ageo uapi-get proba                       # last_handshake_time_sec=<ненулевое> => хендшейк прошёл
ping 10.0.0.1                             # данные через туннель (шлюз VPN-подсети)
ageo stop proba
ageo uninstall proba
```

### «Туннель поднят», но интернет в браузере не через VPN?

Это ожидаемо при `cidr:10.0.0.0/24`: в туннель уходит **только** эта подсеть, весь остальной
трафик (браузер) идёт напрямую — это и спасает RDP. Различай:

- **хендшейк** (`last_handshake_time_sec` ненулевой) = туннель установлен;
- **весь трафик через VPN** = требует `0.0.0.0/0` в `AllowedIPs`.

Чтобы увидеть VPN-трафик:

- завернуть конкретные сайты (фича geo-split): `ageo set-geo proba on geosite:openai`
  или `domain:example.com` — тогда только эти домены идут через VPN. **В браузере отключи
  Secure DNS (DoH)** — иначе он резолвит в обход нашего DNS-прокси и перехват доменов не сработает;
- full-tunnel (`0.0.0.0/0`) — **только за физической/Hyper-V консолью, не по RDP** — egress-IP
  станет серверным. DNS в full-tunnel приложение выставляет само (из строки `DNS=` конфига,
  через `DnsRedirector`), ручных команд не нужно. Проще всего: `scripts/vpn-on.ps1`.

## Ступень 5 — профиль в foreground (АДМИН; узкие маршруты)

```powershell
ageo config-add m1 "C:\путь\conf-1.conf"
ageo set-geo m1 on cidr:10.0.0.0/24
ageo profile-add test m1                  # профиль test поверх конфига m1
ageo profile-run test                     # foreground-цикл; Ctrl+C для остановки
```

В консоли: `connected: ...`; при потере связи — `unreachable ... re-dialing` и повторный connect
(авто-реконнект по liveness). Останавливай **Ctrl+C** — тогда отработает `finally` и погасит
активный туннель. Если закрыть окно крестиком / убить процесс — активная служба-туннель
**останется поднятой**.

`profile-run` — отладочный foreground. Боевой always-on режим — агент-служба (ступень 6).

## Ступень 6 — агент-служба (АДМИН; всегда-онлайн)

`profile-run` живёт только пока открыто окно и не переживает выход/перезагрузку. Боевой режим —
**агент-служба**: одна always-on служба `AmneziaGeoAgent` (LocalSystem, автозапуск), ставится один
раз с элевацией и переживает закрытие UI, разлогин и ребут. Агент гоняет один активный профиль
(профиль = один конфиг), а туннель создаёт/удаляет **эфемерно** (служба туннеля заводится на
connect и сносится на disconnect/стопе). Лог —
`C:\ProgramData\AmneziaGeo\logs\agent.log` (в службе нет консоли).

Команды: `agent-install <target>` (target = имя профиля или конфига), `agent-start`,
`agent-stop`, `agent-status`, `agent-uninstall`.

Одиночный конфиг (профиль из одного), full-tunnel — **только за консолью**:

```powershell
ageo config-add proba "C:\dev\tools\amnezia\amneziageo\.claude\tunnel-template.conf"
ageo set-geo proba off                    # full tunnel
ageo agent-install proba                  # служба AmneziaGeoAgent "--agent proba", start=auto
ageo agent-start
ageo agent-status                         # STATE ... RUNNING
ageo config-list                          # proba RUNNING — туннель подняла служба-член
Get-Content C:\ProgramData\AmneziaGeo\logs\agent.log -Tail 15
ageo agent-stop                           # гасит активный туннель и удаляет его службу
ageo agent-uninstall
```

Профиль — то же самое, только `agent-install <имя-профиля>`:

```powershell
ageo profile-add bal m1
ageo agent-install bal
ageo agent-start
```

Быстрый прогон одиночного конфига — `scripts/agent-test.ps1` (полный туннель, **только за
физической/Hyper-V консолью**); снятие — тот же скрипт с `-Stop`.

## Уборка

```powershell
ageo stop <name>
ageo uninstall <name>
```

Службы туннелей не удаляются автоматически при выходе из приложения — подчищай вручную
(или `scripts/cleanup.ps1`). Под агентом службы-туннели эфемерны: агент сам их сносит на стопе.

## Заметки

- Состояние и имена: одна БД `C:\ProgramData\AmneziaGeo\state.db`, конфиги — в
  `C:\ProgramData\AmneziaGeo\Configurations\<name>.conf`, гео-файлы — в `...\geo\<name>.dat`,
  лог агента — в `...\logs\agent.log`.
- `config-list` показывает состояние службы: `ABSENT` (службы нет), `STOPPED`, `RUNNING`.
- При несовместимом изменении схемы БД `state.db` пересоздаётся пустым (миграций пока нет) —
  гео-источники придётся добавить заново.
