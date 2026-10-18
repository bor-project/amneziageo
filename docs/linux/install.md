---
title: Installation
lang: en
device: linux
topic: install
description: deb packages for servers and desktops, the installer, Docker, updates and uninstalling.
---

# Installation

**English** | [Русский](install.ru.md)

Two parts from one source tree:

- `amneziageo` - the agent that runs as a systemd service, the AmneziaWG engine it drives and the console client `amneziageo` with its full-screen interface. It pulls in no desktop libraries, so it fits a headless server.
- `amneziageo-gui` - the desktop interface. It runs as the desktop user, drives the same agent over its control socket and needs the matching version of `amneziageo`.

A server takes the first package alone, a desktop takes both. Everything the desktop interface configures is also reachable from the console client.

## Installer

The script takes the packages this machine needs straight from the newest release:

```bash
curl -fsSL https://raw.githubusercontent.com/bor-project/amneziageo/master/amneziageo-linux/tools/install.sh | sudo bash
```

It reads the architecture from dpkg, checks every package against the SHA-256 published in `update.json` and hands them to apt. Options: `--no-gui` (agent alone), `--tag v1.2.3.4` (a named release), `--prerelease`, `--repo owner/name`.

## Packages by hand

Take the packages for your architecture from the [Releases](https://github.com/bor-project/amneziageo/releases) page - there are amd64 and arm64 builds - and let apt pull in the shared libraries they name:

```bash
# server
sudo apt install ./amneziageo_<version>_amd64.deb

# desktop
sudo apt install ./amneziageo_<version>_amd64.deb ./amneziageo-gui_<version>_amd64.deb
```

The agent starts right away; it starts at boot while `survive-reboot` is on. The binaries live in `/usr/lib/amneziageo`, the library in `/var/lib/amneziageo`, the client is `/usr/bin/amneziageo`, and the interface name comes from `/etc/default/amneziageo`.

For a machine that is not Debian-based, `amneziageo-linux/tools/install-server.sh` publishes the agent and the console client from the sources straight into `/opt/amneziageo`.

Next: [first configuration](first-config.md).

## Start and stop

```bash
sudo amneziageo start    # start the agent
sudo amneziageo stop     # stop it and drop the tunnel
amneziageo daemon status # running, at boot, control socket
```

Whether the agent starts at boot follows `survive-reboot`: the agent enables or disables its unit at start and whenever the setting changes, from the console, the full-screen console or the window alike. With the setting off a reboot leaves the agent stopped until `sudo amneziageo start`.

## Agent rights

The agent runs as root: it creates the tunnel device and rewrites routes, and `CAP_NET_ADMIN` alone does not satisfy its preflight. The control socket is `/tmp/CoreFxPipe_AmneziaGeo.Agent`, so the unit must not set `PrivateTmp`; every local account that can reach the socket can drive the agent and read the keys of a configuration.

## Update

A packaged build carries the release manifest it checks against, so the app updates itself: the window offers the new version, the agent takes exactly the packages this machine has installed for its own architecture and lets apt install them from a transient unit that outlives the agent restart. When the installed packages are the release before, it fetches one patch per package with the files that changed against the installed ones, a changed file as a delta from the installed one, checks the patch against its published SHA-256 and builds the package again; from an older version, or where the patch does not fit, it downloads the whole package, checks it against the published SHA-256 and carries on after a break. `amneziageo update check` reports the same from the console. Restart the window afterwards to run the new interface too.

## Docker

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

## Uninstalling

`apt remove` keeps the library, `apt purge` deletes it.
