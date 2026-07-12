# AmneziaGeo

> A lightweight AmneziaWG VPN client with geo-aware split tunneling: route only the destinations you choose (by domain, domain category, country, CIDR, or application) through a DPI-resistant tunnel and send everything else direct.

![Platform](https://img.shields.io/badge/platform-Windows%20%28more%20planned%29-0078D6)
![.NET](https://img.shields.io/badge/.NET-10-512BD4)
![UI](https://img.shields.io/badge/UI-Avalonia%2011-8A2BE2)
![Engine](https://img.shields.io/badge/engine-AmneziaWG-2E7D32)
![License](https://img.shields.io/badge/license-GPL--3.0-blue)

AmneziaGeo is a VPN client built on AmneziaWG, the DPI-resistant fork of WireGuard. It is a cross-platform project: the Windows client is available now, with Android, Linux, and Apple clients and a self-hostable server on the roadmap. Unlike a plain WireGuard client, it decides what goes through the tunnel by domain, geosite category, country, CIDR, or application. It keeps that decision correct as CDNs rotate their IPs, and it can tunnel over WebSocket on TCP 443 when plain UDP is blocked.

## The problem it solves

WireGuard and AmneziaWG route purely by IP, using cryptokey routing via a peer's `AllowedIPs`. The protocol has no concept of a domain. Expanding a `geosite:` category into a frozen snapshot of IPs does not hold up in practice: CDNs such as Cloudflare rotate A-records and hand out different IPs per subdomain, so the app reaches an address that was never routed and the traffic leaks direct.

AmneziaGeo solves this with live DNS interception. A local DNS proxy observes the real answers the system receives and programs the tunnel's routed set (`AllowedIPs` plus OS routes) dynamically, before the app opens the connection. It is the sing-box and v2ray idea of routing from real DNS answers, applied on top of WireGuard's cryptokey routing.

`geoip` rules cover whole countries as CIDR ranges and are installed statically into `AllowedIPs` at connect time, because IP ranges do not rotate. The live DNS path is reserved for `geosite` and domain rules, which keeps runtime complexity and route churn low.

## Features

### Geo split tunneling
- Route by geosite (domain categories), geoip (countries), individual domains, CIDR, or application.
- Live DNS interception moves a matched domain onto the tunnel the moment it resolves, without stale IP snapshots or CDN leaks.
- Reusable routing lists: named rule sets shared across configs, each with its own exclusions, all-UDP toggle, and per-list IPv6 opt-in.

### Two tunnel modes
- Full tunnel: everything through the VPN, with an RFC1918 and local-subnet bypass, custom exclusions, and a kill-switch (Windows Filtering Platform).
- Split tunnel: nothing tunneled by default; only matched geo rules take the tunnel, and domains join live as they resolve.
- The VPN endpoint is always excluded from its own tunnel, so the carrier does not loop back on itself.

### DPI resistance and resilient connect
- AmneziaWG obfuscated transport, driven over the WireGuard UAPI.
- WebSocket transport (wstunnel over TCP 443) for networks that drop plain WireGuard UDP.
- DDNS-friendly: the endpoint is resolved before the tunnel comes up, pinned, and falls back to a last-known-good IP, so connect survives DNS capture and flapping.

### Split-horizon DNS
- LAN names resolve on the local resolver, matched names on the tunnel resolver, and everything else direct. The choice is made per query.
- IPv6 is off by default (no v6 leak or blackhole) and can be enabled per routing list for dual-stack.

### Config and UX
- Import a WireGuard or AmneziaWG `.conf`, a QR code, or an AmneziaGeo bundle, and export bundles to share.
- Avalonia UI with light and dark themes and a live RU/EN language switch.
- Built-in diagnostics and log viewer.
- Hot-apply: domain and geoip edits take effect live without a reconnect; a banner asks for a reconnect only when a change genuinely needs one.

### Operations
- Two-process architecture: a long-lived privileged agent service and a transient per-tunnel service, with the UI running unprivileged over IPC.
- Signed MSI installer (WiX Burn bundle) with optional desktop and Start-menu shortcuts, persisted install options, and in-app auto-update.

## How it works

### Three tiers
1. Control plane (C#). Parses `geosite.dat` and `geoip.dat`, resolves rule tokens (`geosite:x` into domain suffixes, `geoip:cc` into CIDRs), generates the config, and runs the DNS proxy and route programmer.
2. Data plane (Go). The AmneziaWG obfuscation engine (`amneziawg-go`), shipped as an opaque per-OS native artifact (`tunnel.dll` on Windows). It is never reimplemented, only driven over the WireGuard UAPI (`set`, `replace_allowed_ips`).
3. Privileged execution. A Windows service hosts the engine and programs the OS route table and firewall, while the UI stays unprivileged.

### Routing by layer
The tunnel releases traffic off itself at three independent layers:

| Layer | Full tunnel | Split tunnel |
|---|---|---|
| AllowedIPs (what the adapter accepts) | `0.0.0.0/0` | geo-rule CIDRs plus resolver /32s |
| OS routes (exclusions to the physical gateway) | endpoint, RFC1918, local subnets, custom | endpoint only |
| DNS proxy (split-horizon) | LAN / tunnel / direct | LAN / tunnel / direct, plus live route on match |
| Kill-switch (WFP) | on | off |

Full tunnel is tunneled by default, with specific exclusions routed back out. Split tunnel is direct by default, with selected traffic routed in and domains added dynamically as the DNS proxy sees them resolve.

## Getting started (Windows)

Requirements: Windows 10 or 11 (x64). The installer sets up a per-machine service and needs administrator rights.

1. Download the latest signed installer from [Releases](../../releases) (`AmneziaGeo-<version>-win-x64-*.exe`).
2. Run it. It installs the AmneziaGeo agent service and the app.
3. Launch AmneziaGeo and import a config: a `.conf` file, a QR code, or a shared bundle.
4. Pick a routing list (or full tunnel) and connect.

The app keeps itself up to date through the built-in updater.

## Beyond geo: apps, UDP, and the fallback proxy

Geo rules are only one kind of entry. The same routing list also drives applications and raw UDP, and when a network blocks UDP outright, a WebSocket proxy can carry the whole tunnel over TCP 443.

### Per-application routing

Add an application to a routing list and only that app's traffic takes the tunnel, regardless of which domains or IPs it reaches. This is useful when you want a single program (a browser profile, a game, a messenger) tunneled while everything else stays direct, or when an app talks to addresses no `geosite` list covers.

Pick the app from the Source dropdown in the routing-list editor:
- Running: choose from live processes and services.
- Installed: choose from installed programs.
- Folder: match every executable under a folder (`app:dir=...`).
- File: match a single executable (`app:path=...`).

Each pick becomes an `app:` rule (`app:dir=`, `app:path=`, or `app:svc=`). The agent watches the owning process and its child processes, such as a browser's separate network process, and pins the IPs that app connects to into the tunnel live, the same way the DNS proxy pins domains. Overly broad matchers (`svchost.exe`, `C:\Windows\...`, a drive root) are rejected, so a rule cannot accidentally tunnel the whole system.

### All UDP through the tunnel

Real-time media such as voice and video calls and online games usually learn their server IPs from in-band signaling rather than DNS, so a domain rule never sees them and the media leaks direct. The per-list "All UDP through the tunnel" toggle routes every outgoing UDP datagram into the tunnel while selective routing is active. The local network and the tunnel's own carrier are excluded, so only the media crosses the VPN.

### WebSocket proxy for blocked UDP

AmneziaWG runs over UDP. On networks that drop or throttle UDP, including much corporate and captive Wi-Fi and some mobile carriers, the handshake never completes. AmneziaGeo can then carry the whole tunnel over a WebSocket on TCP 443, using `wstunnel`. It looks like ordinary HTTPS and passes where UDP cannot.

Enable it per config under Config, Transport, WebSocket proxy, Use proxy:
- Server host: the proxy front, for example your endpoint's DNS name.
- Port: usually 443.
- Auth: None, Basic (login and password), or Token.
- MTU: optional; leave empty for the 1280 default.

It applies on the next connect. Once it is up, the WireGuard UDP rides inside the TCP 443 stream, so even UDP-hostile networks get a working tunnel, and together with All UDP, working voice.

The current WebSocket carrier is a single TCP stream, so bulk and real-time traffic share one queue (head-of-line blocking). An xray-class transport that avoids this is on the [roadmap](#server-and-proxy).

### Example

To tunnel Discord, including voice, on a network that blocks UDP while leaving everything else direct:

1. Create a new list and name it `discord`.
2. Under per-app routing, set Source to Running, pick Discord, and add it. This stores `app:dir=...\Discord`.
3. Turn on All UDP through the tunnel, since Discord's voice servers arrive via signaling rather than DNS.
4. Because the network blocks UDP, open Config, Transport, enable Use proxy, set the host to your endpoint, the port to 443, and Auth as your server requires.
5. Select the `discord` list for the connection and connect.

Discord text and voice now go through the tunnel over TCP 443, and everything else goes direct on the physical link.

## Repository layout

| Directory | Platform | Engine packaging | Status |
|---|---|---|---|
| [`amneziageo-desktop/core`](amneziageo-desktop/core/) | shared (C#) | .NET libraries: geo parsing, config, DNS proxy, UAPI, state | in use |
| [`amneziageo-desktop/windows`](amneziageo-desktop/windows/) | Windows | C# host plus `tunnel.dll` (c-shared) via P/Invoke | beta |
| [`amneziageo-desktop/linux`](amneziageo-desktop/linux/) | Linux | C# plus `amneziawg-go` userspace daemon (UAPI) | planned |
| [`amneziageo-android`](amneziageo-android/) | Android | C# (.NET) `VpnService` plus gomobile `.aar` | planned |
| [`amneziageo-apple`](amneziageo-apple/) | macOS and iOS | native Swift, shared `AmneziaGeoKit` over `libwg-go.a` | planned |

Shared code is organized per language. [`amneziageo-desktop/core`](amneziageo-desktop/core/) is a .NET class library referenced by the Windows, Linux, and Android heads through `<ProjectReference>` (not a submodule), and each head keeps its own solution. `AmneziaGeoKit` (Swift) is shared across the two Apple platforms. Git submodules are reserved for the upstream engines (`amneziawg-windows`, `amneziawg-apple`).

A single C# codebase does not cover every platform, because platform VPN APIs differ. Apple (macOS and iOS) is one native Swift project, since NetworkExtension is native-only. C# covers Windows, Linux, and Android, along with the shared UI and orchestration. The C# and Swift sides share concepts (config and geo logic) rather than code.

## Roadmap

### Platform clients
Windows is available today; the other clients are planned.

- Windows: current focus, a feature-complete beta (see the feature list above).
- Android: C# (.NET) `VpnService` with the engine as a gomobile `.aar`.
- Linux: C# (Avalonia) UI plus an `amneziawg-go` userspace daemon over UAPI, driven by a small privileged helper (systemd, `setcap`), with geo split and a kill-switch via `nftables`. Mostly out-of-process orchestration, no P/Invoke.
- macOS: native Swift over a native NetworkExtension, sharing the `AmneziaGeoKit` core.
- iOS: the same native Swift core (`AmneziaGeoKit`), in one Xcode project shared with macOS.

### Server and proxy
A self-hostable server-side service is planned, so AmneziaGeo is not only a client. It provisions and serves the tunnels that clients connect to.

- AmneziaWG server provisioning: bring up a server and hand out configs.
- Per-device peers: a distinct key, address, and peer slot per device, so two devices never contend for one endpoint.
- Proxy transport: WebSocket and an xray-class transport for stronger DPI resistance and clean UDP and voice, avoiding the head-of-line blocking of a single TCP carrier.
- Config distribution: bundle and QR provisioning for onboarding.

### Client
- Broader per-application and app-aware routing.
- Smarter download routing, moving bulk transfers off the tunnel when it helps.
- More transport options in the UI.
- More UI languages. Shipping today: English and Russian. Planned: Persian, Chinese (Simplified), Arabic, Turkish, Ukrainian, Belarusian, German, French, and Spanish.

## Building from source

Requirements: the .NET 10 SDK, Windows, and the WiX toolset for the installer.

```powershell
# UI and agent
dotnet build amneziageo-desktop/windows/AmneziaGeo.Windows.Ui/AmneziaGeo.Windows.Ui.csproj -c Release

# Full signed installer (MSI plus Burn bundle), output to dist\AmneziaGeo-<version>-win-<arch>-<tag>.exe
amneziageo-desktop/windows/AmneziaGeo.Windows.Installer.Bundle/build-installer.ps1
```

The bundle version is `1.0.1.<git-commit-count>`, so every build is strictly newer to Burn. Combined with the MSI's `MajorUpgrade AllowDowngrades`, a same-version rebuild with different code reinstalls cleanly as an update.

## Tech stack

C# on .NET 10, Avalonia 11 (MVVM, CommunityToolkit), AmneziaWG (`amneziawg-go`, `amneziawg-windows`), SQLite state, and a WiX v4/v5 installer.

## Code signing

Windows release binaries are signed with a certificate issued to the SignPath Foundation. Free code signing is provided by [SignPath.io](https://about.signpath.io/), with a certificate from the [SignPath Foundation](https://signpath.org/).

Signing policy: release artifacts are built only by the GitHub Actions pipeline in this repository, from tagged source, and signed by SignPath as part of that pipeline. No maintainer holds the signing key.

Roles:
- Committers and reviewers: the AmneziaGeo maintainers ([@bor-project](https://github.com/bor-project)).
- Release approvers: the AmneziaGeo maintainers ([@bor-project](https://github.com/bor-project)).

## Privacy

AmneziaGeo collects no analytics or telemetry and sends no personal data to its authors. It makes outbound connections only to:

- the VPN endpoints in the configuration you import;
- the update server it is configured to check for new releases;
- the geo-database sources you configure, to download `geosite` and `geoip` data.

Everything else is routed strictly according to your own rules. No usage data leaves your machine.

## Support

AmneziaGeo is free and open source. If it is useful to you, you can support development with a donation.

- USDT on the TRON network (TRC20): `TNHcrYqUv2pUfW7BEzYJyXfVk9wEJrs4FR`

Send only USDT over the TRON (TRC20) network to this address. Sending a different asset, or using a different network, will lose the funds.

## License

AmneziaGeo is licensed under the GNU General Public License v3.0 or later. See [LICENSE](LICENSE).

It builds on the AmneziaWG engine (`amneziawg-go`, `amneziawg-windows`), licensed by their respective authors.

## Credits

Built on the [Amnezia VPN](https://github.com/amnezia-vpn) ecosystem: the AmneziaWG protocol and engines. AmneziaGeo adds the geo-aware split-tunneling control plane on top.
