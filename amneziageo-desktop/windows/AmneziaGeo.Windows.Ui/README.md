# AmneziaGeo

Справочник по настройке. Файл поставляется рядом с приложением. / Setup reference. This file ships next to the application.

## WebSocket-прокси (wstunnel) / WebSocket (wstunnel) proxy

### English

On the server, install wstunnel and run it as a service. It wraps AmneziaWG UDP into a TLS WebSocket on TCP 443, so the traffic looks like ordinary HTTPS.

Base command:

    wstunnel server wss://0.0.0.0:443 \
      --tls-certificate <fullchain.pem> --tls-private-key <privkey.pem>

- `<fullchain.pem>` / `<privkey.pem>` - the server's existing TLS certificate.
- Open TCP 443 in the firewall. The client tunnels AmneziaWG UDP into this WebSocket.

Authorization (pick one; it must match the client transport settings):

- **Token** - add `--restrict-http-upgrade-path-prefix <token>`. The access token in the client URL must EXACTLY match this value; it also blocks blind scanning.
- **Login and password** - the client sends an `Authorization: Basic` header. Start wstunnel with `--restrict-config <yaml>`, which lists the allowed login/password. They do NOT have to match the AmneziaWG config.
- **None** - restrict the destination instead: `--restrict-to 127.0.0.1:<awg-port>`, where `<awg-port>` is the AmneziaWG endpoint port. The token and `--restrict-to` flags are not used together.

### Русский

На сервере установите wstunnel и запустите его как службу. Он заворачивает UDP AmneziaWG в TLS-WebSocket на TCP 443, и трафик выглядит как обычный HTTPS.

Базовая команда:

    wstunnel server wss://0.0.0.0:443 \
      --tls-certificate <fullchain.pem> --tls-private-key <privkey.pem>

- `<fullchain.pem>` / `<privkey.pem>` - уже имеющийся TLS-сертификат сервера.
- Откройте TCP 443 в фаерволе. Клиент заворачивает UDP AmneziaWG в этот WebSocket.

Авторизация (выберите одну; должна совпадать с настройками транспорта в клиенте):

- **Токен** - добавьте `--restrict-http-upgrade-path-prefix <token>`. Токен доступа в URL клиента должен ТОЧНО совпадать с этим значением; заодно отсекает слепое сканирование.
- **Логин и пароль** - клиент шлёт заголовок `Authorization: Basic`. Запустите wstunnel с `--restrict-config <yaml>`, где указан разрешённый логин/пароль. Совпадать с конфигом AmneziaWG они не обязаны.
- **Без авторизации** - ограничьте назначение: `--restrict-to 127.0.0.1:<awg-port>`, где `<awg-port>` - порт эндпоинта AmneziaWG. Флаги токена и `--restrict-to` вместе не используются.
