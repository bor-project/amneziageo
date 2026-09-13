# Installation

**English** | [Русский](install.ru.md)

Ready-made builds for every platform are on the [Releases](https://github.com/bor-project/amneziageo/releases) page.

## Windows

You need Windows 7, 10 or 11 (x64 or arm64) and administrator rights: the installer sets up a system service.

1. Download an installer from the [Releases](https://github.com/bor-project/amneziageo/releases) page. The regular build carries everything it needs; the smaller variant requires the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) already installed.
2. Run it: the service and the app are installed.
3. Open AmneziaGeo and import a configuration - a `.conf` file, a QR code or a shared settings file.
4. Pick a rule list or the full tunnel and connect.

After that the app updates itself: it offers the new version, downloads the installer for its own architecture and runs it.

The installers are not signed yet, so SmartScreen warns about an unknown publisher and Smart App Control on Windows 11 may block the launch. What to do about it: [CODE_SIGNING.md](../CODE_SIGNING.md).

## Linux

Two parts from one source tree:

- `amneziageo` - the agent that runs as a systemd service, the AmneziaWG engine it drives and the console client `amneziageo` with its full-screen interface. It pulls in no desktop libraries, so it fits a headless server.
- `amneziageo-gui` - the desktop interface. It runs as the desktop user, drives the same agent over its control socket and needs the matching version of `amneziageo`.

A server takes the first package alone, a desktop takes both. Everything the desktop interface configures is also reachable from the console client.

### Installer

The script takes the packages this machine needs straight from the newest release:

```bash
curl -fsSL https://raw.githubusercontent.com/bor-project/amneziageo/master/amneziageo-linux/tools/install.sh | sudo bash
```

It reads the architecture from dpkg, checks every package against the SHA-256 published in `update.json` and hands them to apt. Options: `--no-gui` (agent alone), `--tag v1.2.3.4` (a named release), `--prerelease`, `--repo owner/name`.

### Packages by hand

Take the packages for your architecture from the [Releases](https://github.com/bor-project/amneziageo/releases) page - there are amd64 and arm64 builds - and let apt pull in the shared libraries they name:

```bash
# server
sudo apt install ./amneziageo_<version>_amd64.deb

# desktop
sudo apt install ./amneziageo_<version>_amd64.deb ./amneziageo-gui_<version>_amd64.deb
```

The agent starts and is enabled at boot right away. The binaries live in `/usr/lib/amneziageo`, the library in `/var/lib/amneziageo`, the client is `/usr/bin/amneziageo`, and the interface name comes from `/etc/default/amneziageo`. `apt remove` keeps the library, `apt purge` deletes it.

For a machine that is not Debian-based, `amneziageo-linux/tools/install-server.sh` publishes the agent and the console client from the sources straight into `/opt/amneziageo`.

### First run

```bash
sudo amneziageo geo download
sudo amneziageo config import work --file work.conf
sudo amneziageo up work
sudo amneziageo settings set survive-reboot on
sudo amneziageo settings set periodic-reconnect-enabled on
```

Import also takes `--link` with a `vpn://` URL or `--stdin`.

`up <config>` selects the configuration and connects on it; `select <config>` only remembers it for the next connect. The routing list is one setting for the whole machine, not a per-configuration pairing: `routing use <name>` picks it, `routing use none` leaves the configuration's own `AllowedIPs` to decide what the tunnel carries.

`survive-reboot` dials at agent start, `periodic-reconnect-enabled` redials when the tunnel dies. Without them a reboot or a crashed engine leaves the server without a tunnel.

### Update

A packaged build carries the release manifest it checks against, so the app updates itself: the window offers the new version, the agent downloads exactly the packages this machine has installed for its own architecture, verifies them against the published SHA-256 and lets apt install them from a transient unit that outlives the agent restart. `amneziageo update check` reports the same from the console. Restart the window afterwards to run the new interface too.

### Docker

The agent and the console client also run in a container. `amneziageo-linux/docker/Dockerfile` builds the image from the repository root for amd64 and arm64; the container needs `NET_ADMIN` and `/dev/net/tun`.

```bash
cd amneziageo-linux/docker
mkdir -p configs && cp ~/work.conf configs/
AMNEZIAGEO_CONNECT=work docker compose up -d --build
docker compose exec amneziageo amneziageo geo download
docker compose exec amneziageo amneziageo tui
```

Every `configs/<name>.conf` is imported at start under its file name, and `AMNEZIAGEO_CONNECT` connects on one of them and keeps the tunnel up across restarts. The library lives in a volume, the agent log goes to `docker compose logs`.

- `compose.yaml` - the container keeps a network of its own. Other services join it with `network_mode: service:amneziageo` and reach the world the way the routing list says; such a service loses the network whenever the agent's container restarts and comes back with `docker compose up -d --force-recreate <service>`. The local proxy is published on 10808 (SOCKS5) and 10809 (HTTP): `docker compose exec amneziageo amneziageo proxy on --auth user:password`. Connections that come in through published ports are answered past the tunnel.
- `compose.host.yaml` - `network_mode: host`: the tunnel, the routes and the resolver belong to the host itself. The host's `/etc` is mounted so that the agent points the host's `resolv.conf` at itself (`AMNEZIAGEO_RESOLV_CONF`), and the system bus with AppArmor and SELinux confinement lifted, so that systemd-resolved asks the agent alone while the tunnel is up. It does not run beside an installed agent: both take the same interface and resolver address.

## Android

Android 7.0 or newer.

1. Download `AmneziaGeo-<version>-android.apk` from the [Releases](https://github.com/bor-project/amneziageo/releases) page and allow installation from that source when the system asks.
2. Open the app and import a configuration.
3. On the first connect Android asks for VPN permission - it has to be granted.

The app checks for updates itself and installs the new APK once you agree.
