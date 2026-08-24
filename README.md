# AmneziaGeo

**English** | [Русский](README.ru.md)

![platform](https://img.shields.io/badge/platform-Windows%20%7C%20Linux%20%7C%20Android-0078D6)
![engine](https://img.shields.io/badge/engine-AmneziaWG-2E7D32)
![license](https://img.shields.io/badge/license-GPL--3.0-blue)

A full VPN client on the AmneziaWG engine - a WireGuard fork that is harder to detect and block. It connects to your own server, brings up a network interface, installs routes and applies the DNS from the configuration: everything behind the server is reachable as if you were on its network.

On top of that you decide what goes through the VPN: individual domains, site categories, countries, address ranges or applications. The rest keeps going direct.

## What it is for

Like any WireGuard client, AmneziaGeo gives you an encrypted channel to your own server and access to what sits behind it: the office network, a home NAS, cameras, a printer. The AmneziaWG obfuscation adds resistance to blocking of the protocol itself.

The difference is in how the traffic is chosen. Plain WireGuard routes by IP address and knows nothing about domain names. Expanding a list of domains into addresses once is not enough: large sites live on CDNs and their addresses change constantly. The app reaches an address that is not in the routes yet, and the connection leaks past the VPN.

AmneziaGeo watches the DNS answers. The moment the system learns the address of a domain you picked, the client adds it to the tunnel - before the app opens the connection.

Country rules are built from address ranges and applied at connect time.

## Features

- **Full tunnel** - all traffic through the VPN, with the local network still reachable direct. The kill switch cuts internet access if the tunnel stops working.
- **Split tunnel** - traffic goes direct by default, and only connections that match your rules take the VPN.
- **Flexible routing rules** - by domain, site category, country, address range or application. Rules are grouped into lists, and lists attach to VPN configurations.
- **Access to a remote network** - subnets behind the server are reachable through the full tunnel or through an address-range rule, and the DNS from the configuration resolves internal names.
- **Application routing** - all traffic of a chosen program and its child processes takes the tunnel, whatever addresses it reaches for.
- **All UDP through the tunnel** - for calls, voice chats and games that may learn server addresses without DNS.
- **WebSocket over TCP** - connects on networks where UDP is blocked. It runs on any port and looks like ordinary HTTPS traffic from the outside.
- **Proxy for the local network** - built-in SOCKS5 and HTTP proxies let TVs, phones, consoles and other devices use the tunnel without a VPN client. Ports, accounts or password-free access are configurable, and connected clients are listed.
- **Route and speed check** - a probe by domain or address shows whether the traffic takes the tunnel or goes direct and which rule applied. It also measures latency, jitter, packet loss, download and upload speed.
- **Rules without reconnecting** - domain and country list edits take effect on the fly.

## Supported platforms

- Windows 7, 10 and 11 - x64 and ARM64
- Linux - deb packages for amd64 and arm64
- Android

## Installation

Ready-made builds are on the [Releases](../../releases) page. Per-platform instructions: [docs/install.md](docs/install.md).

## Configuration

Modes, rule lists, application routing and transport selection: [docs/usage.md](docs/usage.md).

## Command line

One command set for every supported platform: [docs/cli.md](docs/cli.md).

## Building from source

Windows, Linux and Android: [docs/build.md](docs/build.md).

## Support the project

If AmneziaGeo is useful to you and you would like to support its development, you can donate in one of the crypto networks:

- **TRON (TRC20):** `TNHcrYqUv2pUfW7BEzYJyXfVk9wEJrs4FR`

Thank you for your support!

## License

GPL-3.0 or later, see [LICENSE](LICENSE). The project uses the AmneziaWG engine under its authors' license.

## Code signing

Windows builds are signed through SignPath.io, see [CODE_SIGNING.md](CODE_SIGNING.md).

Free code signing provided by [SignPath.io](https://signpath.io/), certificate by [SignPath Foundation](https://signpath.org/).

## Privacy

AmneziaGeo collects no user data, see [PRIVACY.md](PRIVACY.md).

## Credits

Built on the [Amnezia VPN](https://github.com/amnezia-vpn) ecosystem: the AmneziaWG protocol and engines. AmneziaGeo adds the choice of what traffic goes through the VPN.
