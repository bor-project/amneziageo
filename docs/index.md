---
title: Overview
lang: en
topic: home
description: AmneziaWG VPN client for Windows, Linux and Android that routes by geosite and geoip databases - site categories, countries, domains, address ranges and applications. Multiple simultaneous VPN connections on Windows.
permalink: /
---

# AmneziaGeo

**English** | [Русский](index.ru.md)

A cross-platform VPN client for Windows, Linux and Android on the AmneziaWG engine - a WireGuard fork that resists blocking. It connects to your own server, brings up a network interface, installs routes and applies the DNS from the configuration: everything behind the server is reachable as if you were on its network.

Split tunneling decides what goes through the VPN: site categories and countries from the geo databases, individual domains, address ranges or applications. The rest keeps going direct. On Windows, you can connect to multiple VPN servers simultaneously and send different routing rules through different servers.

## Choose your device

{% include device-cards.html %}

## Geo databases

A geo database is a `geosite.dat` or `geoip.dat` file in the v2ray and Xray format. `geosite` holds domain categories: services and topics such as `youtube`, `telegram` or `category-ads-all`. `geoip` holds the address ranges of countries and networks: `ru`, `telegram`. A rule names a category, and one line in the list stands for thousands of domains and addresses.

- **Ready databases from the first launch** - the install adds [Loyalsoldier/v2ray-rules-dat](https://github.com/Loyalsoldier/v2ray-rules-dat), the [runetfreedom](https://github.com/runetfreedom/russia-blocked-geosite) databases, [zkeen-ip](https://github.com/jameszeroX/zkeen-ip) and our own [amneziageo-geo](https://github.com/bor-project/amneziageo-geo). The runetfreedom databases ship with the app, so the rules work before the first download.
- **Your own databases** - any `geosite.dat` or `geoip.dat` is added by its direct link. Databases stack: a category comes from the last database in the list that has it, so your own database adds to the common ones instead of replacing them.
- **Updates** - auto-update checks the databases on a schedule and tells you when new versions are out. The check compares the checksum and headers without downloading the file, and the download fetches only the databases that changed. The Windows installer updates the databases during setup.
- **Domains and addresses in one rule** - a category name expands across all databases at once: `geosite:telegram` takes both the Telegram domains and its networks from `geoip`. A country also takes its own domain zones: `geoip:ru` covers the addresses of Russia and sites under `.ru`, `.su` and `.рф`.
- **See what is inside** - the rule editor suggests categories as you type and shows the contents of any of them. In the console, `amneziageo geo list --filter <text>` and `amneziageo geo show <rule>` do the same.
- **Databases from the server** - an AmneziaGeo server sends its geo databases with the configuration answer, and the client adds and downloads them itself.

Our own database fills gaps in the common ones: `geoip:ag-youtube` - YouTube addresses together with video caches, `geoip:ag-discord-voice` - Discord voice servers, `geoip:ag-tiktok` and `geosite:ag-tiktok` - TikTok.

See Geo sources: [Windows](windows/menu/geo-sources.md), [Linux](linux/menu/geo-sources.md), [Android](android/menu/geo-sources.md).

## How the traffic is chosen

Plain WireGuard routes by IP address and knows nothing about domain names. Expanding a list of domains into addresses once is not enough: large sites live on CDNs and their addresses change constantly. The app reaches an address that is not in the routes yet, and the connection leaks past the VPN.

AmneziaGeo watches the DNS answers. The moment the system learns the address of a domain from the rules - named on its own or from a `geosite` category - the client adds it to the tunnel before the app opens the connection. `geoip` ranges and address rules apply at connect time.

Every rule has one of three roles: through the tunnel, direct or block. Rules are grouped into lists, and lists attach to VPN configurations.

## Ready-made lists

A list does not have to be built from scratch: creating one starts from a template.

- **Unavailable sites** - the sites of a ready-made list take the VPN, the rest goes direct.
- **Everything except the chosen region** - everything takes the VPN except the sites and addresses of the countries you mark.
- **Everything through VPN** - all traffic through the tunnel except the home network.
- **Block ads** - adds to any template and blocks the ad domains of `geosite:category-ads-all`.

## Features

- **Split tunnel** - traffic goes direct by default, and only connections that match your rules take the VPN.
- **Full tunnel** - all traffic through the VPN, with the local network still reachable direct. The kill switch cuts internet access if the tunnel stops working.
- **Application routing** - all traffic of a chosen program and its child processes takes the tunnel, whatever addresses it reaches for; in a full tunnel a program can instead be kept direct. See Routing: [Windows](windows/menu/routing.md#application-routing), [Linux](linux/menu/routing.md#application-routing), [Android](android/menu/routing.md#application-routing).
- **Multiple simultaneous VPN connections (Windows)** - keep several tunnels up, choose a main server and reserve servers, and select a server and fallback for each routing rule. See [Multiple VPN connections](windows/recipes/multiple-vpn.md).
- **Holding traffic when the tunnel drops** - while the tunnel reconnects, what the rules send to the VPN waits instead of going direct. See [Windows](windows/menu/home.md#holding-traffic-when-the-tunnel-drops), [Linux](linux/menu/home.md#holding-traffic-when-the-tunnel-drops), [Android](android/menu/home.md#holding-traffic-when-the-tunnel-drops).
- **Rules without reconnecting** - domain and country list edits take effect on the fly.
- **All UDP through the tunnel** - for calls, voice chats and games that may learn server addresses without DNS.
- **WebSocket over TCP** - connects on networks where UDP does not get through.
- **Subscriptions and import** - a configuration is added as a `.conf` file, pasted text, a `vpn://` link, a subscription address or a QR code, and subscriptions refresh themselves. Configurations and routing lists move between devices in one file.
- **AmneziaGeo server** - the client works with any AmneziaWG server, and from an AmneziaGeo server it also gets geo databases, routing lists and a WebSocket entry; a client switched off in the panel is disconnected by the server. See [What the server offers](windows/menu/configurations.md#what-the-server-offers).
- **Access to a remote network** - the office network, a home NAS, cameras and a printer behind the server are reachable through the full tunnel or through an address-range rule, and the DNS from the configuration resolves internal names.
- **Proxy for the local network** - built-in SOCKS5 and HTTP proxies let TVs, phones, consoles and other devices use the tunnel without a VPN client. Ports, accounts or password-free access are configurable, and connected clients are listed.
- **Wi-Fi hotspot (Windows, Linux)** - the computer shares Wi-Fi, and connected devices reach the internet through the tunnel. See [Windows](windows/recipes/hotspot.md), [Linux](linux/recipes/hotspot.md).
- **Route and speed check** - a probe by domain or address shows whether the traffic takes the tunnel or goes direct and which rule applied. It also measures latency, jitter, packet loss, download and upload speed.
- **Console** - the `amneziageo` command manages configurations, lists, geo databases and diagnostics on all three platforms. See [Windows](windows/cli.md), [Linux](linux/cli.md), [Android](android/cli.md).

## Supported platforms

- Windows 7, 10 and 11 - x64 and ARM64
- Linux - deb packages for amd64 and arm64
- Android

The multiple-VPN mode is currently available on Windows, the Wi-Fi hotspot on Windows and Linux. Linux and Android use one active VPN connection; the client, the geo databases and routing are available on all three platforms.

Ready-made builds are on the [Releases](https://github.com/bor-project/amneziageo/releases) page.

## For developers

Windows, Linux and Android: [Building from source](develop/build.md).

## Support the project

If AmneziaGeo is useful to you and you would like to support its development, you can donate in one of the crypto networks:

- **TRON (TRC20):** `TNHcrYqUv2pUfW7BEzYJyXfVk9wEJrs4FR`

Thank you for your support!

## License

GPL-3.0 or later, see [LICENSE](https://github.com/bor-project/amneziageo/blob/master/LICENSE). The project uses the AmneziaWG engine under its authors' license.

## Code signing

Windows builds are not signed yet: the code signing certificate is being issued. Until then SmartScreen warns about an unknown publisher.

<!--
Windows builds are signed through SignPath.io, see [code signing policy](../CODE_SIGNING.md).

Free code signing provided by [SignPath.io](https://signpath.io/), certificate by [SignPath Foundation](https://signpath.org/).
-->

## Privacy

AmneziaGeo collects no user data, see [privacy policy](../PRIVACY.md).

## Credits

Built on the [Amnezia VPN](https://github.com/amnezia-vpn) ecosystem: the AmneziaWG protocol and engines. AmneziaGeo adds choosing traffic by geo databases.

Default geo databases: [Loyalsoldier/v2ray-rules-dat](https://github.com/Loyalsoldier/v2ray-rules-dat), [runetfreedom](https://github.com/runetfreedom), [zkeen-ip](https://github.com/jameszeroX/zkeen-ip).
