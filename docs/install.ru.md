# Установка

[English](install.md) | **Русский**

Готовые сборки для всех платформ - на странице [Releases](https://github.com/bor-project/amneziageo/releases).

## Windows

Нужны Windows 7, 10 или 11 (x64 или arm64) и права администратора: установщик ставит системную службу.

1. Скачайте установщик со страницы [Releases](https://github.com/bor-project/amneziageo/releases). Обычная сборка несёт в себе всё необходимое, вариант поменьше требует установленного [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).
2. Запустите его: установятся служба и приложение.
3. Откройте AmneziaGeo и импортируйте конфигурацию - файл `.conf`, QR-код или общий файл настроек.
4. Выберите список правил или полный туннель и подключитесь.

Дальше приложение обновляется само: предлагает новую версию, качает установщик своей архитектуры и запускает его.

Установщики пока не подписаны, поэтому SmartScreen предупреждает о неизвестном издателе, а Smart App Control на Windows 11 может заблокировать запуск. Что с этим делать - в [CODE_SIGNING.md](../CODE_SIGNING.md).

## Linux

Две части из одного дерева исходников:

- `amneziageo` - агент службой systemd, движок AmneziaWG и консольный клиент `amneziageo` с полноэкранным интерфейсом. Графических библиотек не тянет, годится серверу без рабочего стола.
- `amneziageo-gui` - графический интерфейс. Работает от пользователя рабочего стола, управляет тем же агентом через его контрольный сокет и требует `amneziageo` той же версии.

Серверу нужен только первый пакет, рабочему столу - оба. Всё, что настраивается в графическом интерфейсе, доступно и из консольного клиента.

### Установщик

Скрипт берёт нужные машине пакеты прямо из свежего релиза:

```bash
curl -fsSL https://raw.githubusercontent.com/bor-project/amneziageo/master/amneziageo-linux/tools/install.sh | sudo bash
```

Архитектуру он читает из dpkg, каждый пакет сверяет с SHA-256 из `update.json` и отдаёт apt. Ключи: `--no-gui` (только агент), `--tag v1.2.3.4` (конкретный релиз), `--prerelease`, `--repo owner/name`.

### Пакеты вручную

Возьмите пакеты своей архитектуры со страницы [Releases](https://github.com/bor-project/amneziageo/releases) - собираются amd64 и arm64 - и поставьте через apt, он подтянет названные ими библиотеки:

```bash
# сервер
sudo apt install ./amneziageo_<версия>_amd64.deb

# рабочий стол
sudo apt install ./amneziageo_<версия>_amd64.deb ./amneziageo-gui_<версия>_amd64.deb
```

Агент сразу запускается и включается в автозагрузку. Бинарники лежат в `/usr/lib/amneziageo`, библиотека - в `/var/lib/amneziageo`, клиент - `/usr/bin/amneziageo`, имя интерфейса агент берёт из `/etc/default/amneziageo`. `apt remove` библиотеку оставляет, `apt purge` удаляет.

Для машины не на Debian `amneziageo-linux/tools/install-server.sh` публикует агент и консольный клиент из исходников прямо в `/opt/amneziageo`.

### Первый запуск

```bash
sudo amneziageo geo download
sudo amneziageo config import work --file work.conf     # либо --link 'vpn://…', либо --stdin
sudo amneziageo up work
sudo amneziageo settings set survive-reboot on
sudo amneziageo settings set periodic-reconnect-enabled on
```

`up <конфиг>` выбирает конфигурацию и подключается на ней, `select <конфиг>` только запоминает её для следующего подключения. Список маршрутизации - одна настройка на всю машину, а не привязка к конфигурации: `routing use <имя>` выбирает список, `routing use none` оставляет решение за собственными `AllowedIPs` конфигурации.

`survive-reboot` поднимает туннель при старте агента, `periodic-reconnect-enabled` переподнимает его, если туннель упал. Без них перезагрузка или падение движка оставляют сервер без туннеля.

### Обновление

Пакет несёт в себе адрес манифеста релизов, поэтому приложение обновляет себя само: окно предлагает новую версию, агент качает ровно те пакеты, что стоят на машине, под её архитектуру, сверяет их с опубликованной SHA-256 и отдаёт apt из временного юнита, который переживает перезапуск самого агента. То же самое из консоли - `amneziageo update check`. После установки перезапустите окно, чтобы обновился и интерфейс.

### Docker

Агент и консольный клиент работают и в контейнере. `amneziageo-linux/docker/Dockerfile` собирает образ из корня репозитория под amd64 и arm64; контейнеру нужны `NET_ADMIN` и `/dev/net/tun`.

```bash
cd amneziageo-linux/docker
mkdir -p configs && cp ~/work.conf configs/
AMNEZIAGEO_CONNECT=work docker compose up -d --build
docker compose exec amneziageo amneziageo geo download
docker compose exec amneziageo amneziageo tui
```

Каждый `configs/<имя>.conf` при старте импортируется под именем файла, а `AMNEZIAGEO_CONNECT` подключается на одном из них и держит туннель после перезапусков. Библиотека лежит в томе, журнал агента выводит `docker compose logs`.

- `compose.yaml` - у контейнера своя сеть. Другие сервисы входят в неё через `network_mode: service:amneziageo` и ходят туда, куда их отправляет список маршрутизации; при каждом перезапуске контейнера агента такой сервис теряет сеть и возвращается через `docker compose up -d --force-recreate <сервис>`. Локальный прокси опубликован на 10808 (SOCKS5) и 10809 (HTTP): `docker compose exec amneziageo amneziageo proxy on --auth user:password`. На соединения, пришедшие через опубликованные порты, ответ уходит мимо туннеля.
- `compose.host.yaml` - `network_mode: host`: туннель, маршруты и резолвер принадлежат самому хосту. `/etc` хоста примонтирован, чтобы агент направил на себя `resolv.conf` хоста (`AMNEZIAGEO_RESOLV_CONF`), а системная шина со снятыми ограничениями AppArmor и SELinux - чтобы systemd-resolved, пока туннель поднят, спрашивал только агента. Рядом с установленным агентом не запускается: оба занимают один и тот же интерфейс и адрес резолвера.

## Android

Нужен Android 7.0 или новее.

1. Скачайте `AmneziaGeo-<версия>-android.apk` со страницы [Releases](https://github.com/bor-project/amneziageo/releases) и разрешите установку из этого источника, когда система спросит.
2. Откройте приложение и импортируйте конфигурацию.
3. При первом подключении Android спросит разрешение на VPN - его нужно выдать.

Обновления приложение проверяет само и ставит новый APK с вашего согласия.
