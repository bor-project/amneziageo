---
title: Overview
lang: en
section: overview
description: Cross-platform AmneziaWG VPN client with split tunneling and traffic routing for Windows, Linux and Android. Multiple simultaneous VPN connections on Windows.
translation: /index.ru.html
permalink: /
---

# AmneziaGeo

**English** | [Русский](index.ru.md)

A cross-platform VPN client for Windows, Linux and Android on the AmneziaWG engine - a WireGuard fork that is harder to detect and block. It connects to your own server, brings up a network interface, installs routes and applies the DNS from the configuration: everything behind the server is reachable as if you were on its network.

Split tunneling lets you decide what goes through the VPN: individual domains, site categories, countries, address ranges or applications. The rest keeps going direct. On Windows, you can connect to multiple VPN servers simultaneously and send different routing rules through different servers.

## What it is for

Like any WireGuard client, AmneziaGeo gives you an encrypted channel to your own server and access to what sits behind it: the office network, a home NAS, cameras, a printer. The AmneziaWG obfuscation adds resistance to blocking of the protocol itself.

The difference is in how the traffic is chosen. Plain WireGuard routes by IP address and knows nothing about domain names. Expanding a list of domains into addresses once is not enough: large sites live on CDNs and their addresses change constantly. The app reaches an address that is not in the routes yet, and the connection leaks past the VPN.

AmneziaGeo watches the DNS answers. The moment the system learns the address of a domain you picked, the client adds it to the tunnel - before the app opens the connection.

Country rules are built from address ranges and applied at connect time.

## Features

- **Multiple simultaneous VPN connections (Windows)** - keep several tunnels up, choose a main server and reserve servers, and select a server and fallback for each routing rule. See [configuration](usage.md#multiple-vpn-connections-windows).
- **Full tunnel** - all traffic through the VPN, with the local network still reachable direct. The kill switch cuts internet access if the tunnel stops working.
- **Split tunnel** - traffic goes direct by default, and only connections that match your rules take the VPN.
- **Flexible routing rules** - by domain, site category, country, address range or application. Rules are grouped into lists, and lists attach to VPN configurations.
- **Access to a remote network** - subnets behind the server are reachable through the full tunnel or through an address-range rule, and the DNS from the configuration resolves internal names.
- **Application routing (experimental)** - all traffic of a chosen program and its child processes takes the tunnel, whatever addresses it reaches for. The controls are hidden by default; see [configuration](usage.md#application-routing).
- **All UDP through the tunnel** - for calls, voice chats and games that may learn server addresses without DNS.
- **WebSocket over TCP** - connects on networks where UDP is blocked. It runs on any port and looks like ordinary HTTPS traffic from the outside.
- **Proxy for the local network** - built-in SOCKS5 and HTTP proxies let TVs, phones, consoles and other devices use the tunnel without a VPN client. Ports, accounts or password-free access are configurable, and connected clients are listed.
- **Route and speed check** - a probe by domain or address shows whether the traffic takes the tunnel or goes direct and which rule applied. It also measures latency, jitter, packet loss, download and upload speed.
- **Rules without reconnecting** - domain and country list edits take effect on the fly.

## Supported platforms

- Windows 7, 10 and 11 - x64 and ARM64
- Linux - deb packages for amd64 and arm64
- Android

The multiple-VPN mode is currently available on Windows. Linux and Android use one active VPN connection; the client and its routing features are available on all three platforms.

## Installation

Ready-made builds are on the [Releases](https://github.com/bor-project/amneziageo/releases) page. Per-platform instructions: [Installation](install.md).

## Configuration

Modes, rule lists, application routing and transport selection: [Configuration](usage.md).

## Command line

A shared core command set, with platform-specific extensions: [Command line](cli.md).

## Building from source

Windows, Linux and Android: [Building from source](build.md).

## Support the project

If AmneziaGeo is useful to you and you would like to support its development, you can donate in one of the crypto networks:

- **TRON (TRC20):** `TNHcrYqUv2pUfW7BEzYJyXfVk9wEJrs4FR`

Thank you for your support!

## License

GPL-3.0 or later, see [LICENSE](https://github.com/bor-project/amneziageo/blob/master/LICENSE). The project uses the AmneziaWG engine under its authors' license.

## Code signing

Windows builds are signed through SignPath.io, see [code signing policy](../CODE_SIGNING.md).

Free code signing provided by [SignPath.io](https://signpath.io/), certificate by [SignPath Foundation](https://signpath.org/).

## Privacy

AmneziaGeo collects no user data, see [privacy policy](../PRIVACY.md).

## Credits

Built on the [Amnezia VPN](https://github.com/amnezia-vpn) ecosystem: the AmneziaWG protocol and engines. AmneziaGeo adds the choice of what traffic goes through the VPN.
