# AmneziaGeo

[English](README.md) | **Русский**

![platform](https://img.shields.io/badge/platform-Windows%20%7C%20Linux%20%7C%20Android-0078D6)
![engine](https://img.shields.io/badge/engine-AmneziaWG-2E7D32)
![license](https://img.shields.io/badge/license-GPL--3.0-blue)

AmneziaGeo - кроссплатформенный VPN-клиент для Windows, Linux и Android на базе AmneziaWG, доработанного WireGuard. Раздельное туннелирование (split tunneling) направляет выбранный трафик через ваши серверы. На Windows можно подключить несколько VPN одновременно и выбрать сервер для каждого правила маршрутизации.

**[Скачать](https://github.com/bor-project/amneziageo/releases) · [Документация](https://bor-project.github.io/amneziageo/index.ru.html)**

## Возможности

- Несколько VPN одновременно на Windows: основной и резервные серверы, выбор сервера для каждого правила.
- Полный и раздельный туннель по доменам, категориям сайтов, странам, диапазонам IP и приложениям; правила доменов и стран обновляются без переподключения.
- Маршрутизация доменов по DNS и доступ к сетям за серверами. Маршрутизация приложений экспериментальная; подробности - в [настройке](https://bor-project.github.io/amneziageo/usage.ru.html#маршрутизация-приложений).
- WebSocket поверх TCP для сетей, блокирующих UDP.
- SOCKS5- и HTTP-прокси для других устройств в локальной сети.
- Диагностика маршрутов, проверка скорости и единый интерфейс командной строки.

Windows 7, 10 и 11 (x64 и ARM64), Linux (пакеты deb для amd64 и arm64) и Android.

Установка, настройка, команды CLI и сборка из исходников - на **[сайте документации](https://bor-project.github.io/amneziageo/index.ru.html)**.

## Поддержать проект

Пожертвования через **TRON (TRC20):** `TNHcrYqUv2pUfW7BEzYJyXfVk9wEJrs4FR`.

## Лицензия и благодарности

[GPL-3.0 или новее](LICENSE). Проект построен на экосистеме [Amnezia VPN](https://github.com/amnezia-vpn); движок AmneziaWG используется на условиях лицензии его авторов.

Бесплатная подпись кода предоставлена [SignPath.io](https://signpath.io/), сертификат - [SignPath Foundation](https://signpath.org/). Подробнее: [политика подписи кода](https://bor-project.github.io/amneziageo/CODE_SIGNING.html) и [политика конфиденциальности](https://bor-project.github.io/amneziageo/PRIVACY.html) на английском языке.
