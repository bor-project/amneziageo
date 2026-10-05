# AmneziaGeo

**English** | [Русский](README.ru.md)

![platform](https://img.shields.io/badge/platform-Windows%20%7C%20Linux%20%7C%20Android-0078D6)
![engine](https://img.shields.io/badge/engine-AmneziaWG-2E7D32)
![license](https://img.shields.io/badge/license-GPL--3.0-blue)

AmneziaGeo is a cross-platform VPN client for Windows, Linux and Android, built on AmneziaWG - a WireGuard fork. Use split tunneling to route selected traffic through your own servers. On Windows, connect to multiple VPN servers at once and choose a server for each routing rule.

**[Download](https://github.com/bor-project/amneziageo/releases) · [Documentation](https://bor-project.github.io/amneziageo/)**

## Features

- Multiple simultaneous VPN connections on Windows, with a main server, reserve servers and per-rule server selection.
- Full and split tunneling by domain, site category, country, IP range or application; domain and country rules update without reconnecting.
- DNS-based domain routing and access to networks behind your servers. Application routing is experimental; see [configuration](https://bor-project.github.io/amneziageo/usage.html#application-routing).
- WebSocket over TCP for networks that block UDP.
- SOCKS5 and HTTP proxies for other devices on the local network.
- Route diagnostics, speed checks and a shared command-line interface.

Windows 7, 10 and 11 (x64 and ARM64), Linux (deb packages for amd64 and arm64), and Android.

Installation, configuration, command-line reference and build instructions are on the **[documentation site](https://bor-project.github.io/amneziageo/)**.

## Support the project

Donations via **TRON (TRC20):** `TNHcrYqUv2pUfW7BEzYJyXfVk9wEJrs4FR`.

## License and credits

[GPL-3.0 or later](LICENSE). Built on the [Amnezia VPN](https://github.com/amnezia-vpn) ecosystem; the AmneziaWG engine retains its authors' license.

Free code signing provided by [SignPath.io](https://signpath.io/), certificate by [SignPath Foundation](https://signpath.org/). See the [code signing policy](https://bor-project.github.io/amneziageo/CODE_SIGNING.html) and [privacy policy](https://bor-project.github.io/amneziageo/PRIVACY.html).
