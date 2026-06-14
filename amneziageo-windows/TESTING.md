# Тестирование AmneziaGeo (Windows) — отладочный прогон

Консольный хост `AmneziaGeo.Windows.App` — это набор подкоманд. Документ описывает ручную
проверку слоёв до появления UI.

## Требования

- .NET 10 SDK, собранный проект (Debug).
- **Запуск от администратора** для команд, создающих службы/туннели/маршруты
  (`install` / `start` / `stop` / `uninstall` / `balancer-run`). Команды настроек, конфигов и гео
  админа не требуют.
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
- `cleanup.ps1` — убирает тестовые конфиги/службы, дубли гео-источников, сбрасывает DNS.

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

## Ступень 5 — балансировщик (АДМИН; узкие маршруты)

```powershell
ageo config-add m1 "C:\путь\conf-1.conf"
ageo config-add m2 "C:\путь\conf-2.conf"
ageo set-geo m1 on cidr:10.0.0.0/24
ageo set-geo m2 on cidr:10.0.0.0/24
ageo balancer-add test 30 m1 m2          # recheck 30с, приоритет m1 > m2
ageo balancer-run test                   # foreground-цикл; Ctrl+C для остановки
```

В консоли: `connected: ...`, `member ... unreachable; failing over`, `switched to ...`.
Останавливай **Ctrl+C** — тогда отработает `finally` и погасит активного члена. Если закрыть
окно крестиком / убить процесс — активная служба-туннель **останется поднятой**.

## Уборка

```powershell
ageo stop <name>
ageo uninstall <name>
```

Службы туннелей не удаляются автоматически при выходе из приложения — подчищай вручную.

## Заметки

- Состояние и имена: одна БД `C:\ProgramData\AmneziaGeo\state.db`, конфиги — в
  `C:\ProgramData\AmneziaGeo\Configurations\<name>.conf`, гео-файлы — в `...\geo\<name>.dat`.
- `config-list` показывает состояние службы: `ABSENT` (службы нет), `STOPPED`, `RUNNING`.
- При несовместимом изменении схемы БД `state.db` пересоздаётся пустым (миграций пока нет) —
  гео-источники придётся добавить заново.
