# AmneziaGeo

**English** · [Русский](README.ru.md)

> Lightweight **AmneziaWG** VPN client with **geo-aware split tunneling** — route only the destinations you choose (by domain, domain category, country, CIDR or application) through a DPI-resistant tunnel, and send everything else direct.

![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D6)
![.NET](https://img.shields.io/badge/.NET-10-512BD4)
![UI](https://img.shields.io/badge/UI-Avalonia%2011-8A2BE2)
![Engine](https://img.shields.io/badge/engine-AmneziaWG-2E7D32)
![License](https://img.shields.io/badge/license-GPL--3.0-blue)

AmneziaGeo is a desktop VPN client built on **AmneziaWG** — the DPI-resistant fork of WireGuard. Unlike a plain WireGuard client, it decides *what* goes through the tunnel by **domain, geosite category, country, CIDR or application**, keeps that decision correct as CDNs rotate their IPs, and punches through hostile networks by tunneling over WebSocket/TCP 443 when plain UDP is blocked.

---

## The problem it solves

WireGuard (and AmneziaWG) routes purely by **IP** — *cryptokey routing* via a peer's `AllowedIPs`. The protocol has **no concept of a domain**. Naively expanding a `geosite:` category into a frozen snapshot of IPs breaks in practice: CDNs (Cloudflare and friends) rotate A-records and hand out different IPs per subdomain, so the app hits an address that was never routed and the traffic leaks direct.

AmneziaGeo fixes this with **live DNS interception**. A local DNS proxy observes the *real* answers the system receives and programs the tunnel's routed set (`AllowedIPs` + OS routes) **dynamically, before the app opens the connection** — the sing-box / v2ray "route from real DNS" idea, kept on top of WireGuard's cryptokey routing.

**Key rule:** `geoip` (country → CIDR) is **preinstalled statically** into `AllowedIPs` at connect time — IP ranges don't rotate. The **live DNS path is reserved for `geosite` / domains**, which halves runtime complexity and route churn.

---

## Features

### Geo split tunneling
- Route by **geosite** (domain categories), **geoip** (countries), individual **domains**, **CIDR**, or **application**.
- **Live DNS interception** re-points a matched domain onto the tunnel the moment it resolves — no stale IP snapshots, no CDN leaks.
- **Reusable routing lists** — named rule sets shared across configs, each with its own exclusions, all-UDP toggle, IPv6 opt-in and mode.

### Two tunnel modes
- **Full tunnel** — everything through the VPN, with an RFC1918 / local-subnet **bypass**, custom exclusions, and a **kill-switch** (Windows Filtering Platform).
- **Split tunnel** — nothing tunneled by default; only matched geo rules take the tunnel. Domains join live as they resolve.
- The VPN **endpoint is always excluded** from its own tunnel, so the carrier never loops back on itself.

### DPI resistance & resilient connect
- **AmneziaWG** obfuscated transport, driven over the WireGuard **UAPI**.
- **WebSocket transport** (wstunnel over TCP/443) for networks that drop plain WireGuard UDP.
- **DDNS-friendly**: the endpoint is resolved pre-tunnel, **pinned**, and falls back to a **last-known-good** IP, so connect survives DNS capture and flapping.

### Split-horizon DNS
- LAN names resolve on the **local** resolver, matched names on the **tunnel** resolver, everything else **direct** — three-way, per query.
- **IPv6** is v4-only by default (no v6 leak/blackhole) and **opt-in per routing list** for dual-stack.

### Config & UX
- Import a WireGuard / AmneziaWG **`.conf`**, a **QR code**, or an AmneziaGeo **bundle**; export bundles to share.
- **Avalonia** UI with light / dark themes and **live language switch** (RU / EN).
- Built-in **diagnostics & log viewer**.
- **Hot-apply**: domain / geoip edits take effect live without a reconnect; a banner asks for a reconnect only when the change genuinely needs one.

### Operations
- **Two-process architecture** — a long-lived privileged agent service plus a transient per-tunnel service; the UI runs unprivileged over IPC.
- **Signed MSI installer** (WiX Burn bundle) with optional desktop / Start-menu shortcuts, **persisted install options**, and **in-app auto-update**.

---

## How it works

### Three tiers
1. **Control plane — C#.** Parses `geosite.dat` / `geoip.dat`, resolves rule tokens (`geosite:x` → domain suffixes, `geoip:cc` → CIDRs), generates the config, and runs the DNS proxy + route programmer.
2. **Data plane — Go.** The AmneziaWG obfuscation engine (`amneziawg-go`), shipped as an **opaque per-OS native artifact** (`tunnel.dll` on Windows). Never reimplemented; driven over the WireGuard UAPI (`set` / `replace_allowed_ips`).
3. **Privileged execution.** A Windows service hosts the engine and programs the OS route table and firewall; the UI stays unprivileged.

### Routing decision, per layer
The tunnel "leaks" traffic off itself at three independent layers:

| Layer | Full tunnel | Split tunnel |
|---|---|---|
| **AllowedIPs** (what the adapter accepts) | `0.0.0.0/0` | only geo-rule CIDRs + resolver /32s |
| **OS routes** (exclusions to the physical gateway) | endpoint + RFC1918 + local subnets + custom | endpoint only |
| **DNS proxy** (split-horizon) | LAN / tunnel / direct | LAN / tunnel / direct + live route-on-match |
| **Kill-switch** (WFP) | on | off |

In short: full-tunnel means *tunnel by default, punch holes out*; split-tunnel means *direct by default, drive selected traffic in* — and domains are driven in dynamically as the DNS proxy sees them resolve.

---

## Getting started (Windows)

**Requirements:** Windows 10 / 11 (x64). The installer sets up a per-machine service and needs administrator rights.

1. Download the latest signed installer from **[Releases](../../releases)** (`AmneziaGeo-<version>-win-x64-*.exe`).
2. Run it — it installs the AmneziaGeo agent service and the app.
3. Launch AmneziaGeo, **import a config** (`.conf` file, QR code, or a shared bundle).
4. Pick a **routing list** (or full-tunnel) and connect.

The app keeps itself up to date via the built-in updater.

---

## Beyond geo: apps, UDP and the fallback proxy

Geo rules are only one kind of entry. The same routing list also drives **applications** and **raw UDP**, and — when a network blocks UDP outright — a **WebSocket proxy** carries the whole tunnel over TCP 443.

### Per-application routing

Add an **application** to a routing list and only *that app's* traffic takes the tunnel, regardless of which domains or IPs it hits. Useful when you want one program (a browser profile, a game, a messenger) tunneled while everything else stays direct, or when an app talks to addresses no `geosite` list covers.

Pick the app from the **Source** dropdown in the routing-list editor:
- **Running** — choose from live processes (and services);
- **Installed** — choose from installed programs;
- **Folder** — match every executable under a folder (`app:dir=…`);
- **File** — match a single executable (`app:path=…`).

Each pick becomes an `app:` rule (`app:dir=` / `app:path=` / `app:svc=`). The agent watches the owning process **and its child processes** (e.g. a browser's separate network process) and **pins the IPs that app connects to** into the tunnel live — the same mechanism the DNS proxy uses for domains. Overly-broad matchers (`svchost.exe`, `C:\Windows\…`, a drive root) are refused, so a rule can't accidentally tunnel the whole system.

### All UDP through the tunnel

Real-time media — **voice / video calls, online games** — usually learn their server IPs from in-band signaling, not DNS, so a domain rule never sees them and the media leaks direct. The per-list **All UDP through the tunnel** toggle routes *every* outgoing UDP datagram into the tunnel while selective routing is active. The local network and the tunnel's own carrier are excluded, so only the media crosses the VPN.

### WebSocket proxy — when UDP is blocked

AmneziaWG is UDP. On networks that drop or throttle UDP (much corporate / captive Wi-Fi, some mobile carriers), the handshake never completes. AmneziaGeo can then carry the whole tunnel **over a WebSocket on TCP 443** (via `wstunnel`) — it looks like ordinary HTTPS and passes where UDP can't.

Enable it per config in **Config → Transport → WebSocket proxy → Use proxy**:
- **Server host** — the proxy front (e.g. your endpoint's DNS name);
- **Port** — usually **443**;
- **Auth** — **None**, **Basic** (login / password), or **Token**;
- **MTU** — optional; leave empty for the 1280 default.

It applies on the next connect. Once up, the WireGuard UDP rides inside the TCP/443 stream, so even UDP-hostile networks get a working tunnel — and, combined with **All UDP**, working voice.

> The current WebSocket carrier is a single TCP stream, so bulk and real-time traffic share one queue (head-of-line blocking). An **xray-class transport** that avoids this is on the [roadmap](#server--proxy).

### Putting it together — a worked example

Goal: tunnel **Discord** (voice included) through the VPN on a network that blocks UDP, and leave everything else direct.

1. **New list** → name it `discord`.
2. **Per-app routing** → Source **Running** → pick **Discord** → **Add** (adds `app:dir=…\Discord`).
3. Turn on **All UDP through the tunnel** — Discord's voice servers arrive via signaling, not DNS.
4. The network blocks UDP, so open **Config → Transport**, enable **Use proxy**, set host = your endpoint, port = **443**, and Auth as your server requires.
5. Select the `discord` list for the connection and connect.

Result: Discord (text + voice) is tunneled over TCP 443; everything else goes direct on the physical link.

---

## Repository layout

| Directory | Platform | Engine packaging | Status |
|---|---|---|---|
| [`amneziageo-desktop/core`](amneziageo-desktop/core/) | shared (C#) | .NET libraries — geo parsing, config, DNS proxy, UAPI, state | ✅ in use |
| [`amneziageo-desktop/windows`](amneziageo-desktop/windows/) | Windows | C# host + `tunnel.dll` (c-shared) via P/Invoke | ✅ **beta** |
| [`amneziageo-desktop/linux`](amneziageo-desktop/linux/) | Linux | C# + `amneziawg-go` userspace daemon (UAPI) | 📋 planned |
| [`amneziageo-android`](amneziageo-android/) | Android | C# (.NET) `VpnService` + gomobile `.aar` | 📋 planned |
| [`amneziageo-apple`](amneziageo-apple/) | macOS + iOS | native Swift — shared `AmneziaGeoKit` over `libwg-go.a` | 📋 planned |

Shared code is **per language**: [`amneziageo-desktop/core`](amneziageo-desktop/core/) is a .NET class library referenced by the Windows / Linux / Android heads via `<ProjectReference>` (not a submodule); each head keeps its own solution. `AmneziaGeoKit` (Swift) is shared across the two Apple platforms. Git submodules are reserved for the upstream engines (`amneziawg-windows`, `amneziawg-apple`).

> Honest scope note: "one C# codebase for everything" does not survive contact with platform VPN APIs. **Apple (macOS + iOS) is a single native Swift project** — NetworkExtension is native-only. C# owns Windows, Linux, Android, and all desktop UI / orchestration. The two language worlds share **concepts** (config, geo logic), not code.

---

## Roadmap

### Platform clients
Windows ships today; the remaining clients are planned:

- **Windows** — ✅ current focus; feature-complete beta (see the feature list above).
- **Android** — 📋 planned — C# (.NET) `VpnService` with the engine as a gomobile `.aar`.
- **Linux** — 📋 planned — C# (Avalonia) UI + `amneziawg-go` userspace daemon over UAPI, driven by a small privileged helper (systemd / `setcap`); geo split, kill-switch via `nftables`. Mostly out-of-process orchestration, no P/Invoke.
- **macOS** — 📋 planned — native Swift over a native NetworkExtension, sharing the `AmneziaGeoKit` core.
- **iOS** — 📋 planned — the same native Swift core (`AmneziaGeoKit`), one Xcode project shared with macOS.

### Server & proxy
A self-hostable **server-side service** is planned so AmneziaGeo isn't only a client — it provisions and serves the tunnels the clients connect to:
- **AmneziaWG server provisioning** — turn up a server and hand out configs.
- **Per-device peers** — a distinct key / address / peer slot per device, so two devices never fight over one endpoint.
- **Proxy transport** — WebSocket and an **xray-class** transport for strong DPI resistance and clean **UDP / voice** (avoiding the head-of-line blocking of a single TCP carrier).
- **Config distribution** — bundle / QR provisioning for onboarding.

### Client
- Broader per-application routing and app-aware rules.
- Smarter download routing (bulk transfers off-tunnel when it helps).
- More transport options surfaced in the UI.

---

## Building from source

Requirements: **.NET 10 SDK**, Windows, and the WiX toolset for the installer.

```powershell
# UI + agent
dotnet build amneziageo-desktop/windows/AmneziaGeo.Windows.Ui/AmneziaGeo.Windows.Ui.csproj -c Release

# Full signed installer (MSI + Burn bundle) -> dist\AmneziaGeo-<version>-win-<arch>-<tag>.exe
amneziageo-desktop/windows/AmneziaGeo.Windows.Installer.Bundle/build-installer.ps1
```

The bundle version is `1.0.1.<git-commit-count>`, so every build is strictly newer to Burn; combined with the MSI's `MajorUpgrade AllowDowngrades`, "same version, different code" reinstalls cleanly as an update.

## Tech stack

C# / **.NET 10** · **Avalonia 11** (MVVM / CommunityToolkit) · **AmneziaWG** (`amneziawg-go` / `amneziawg-windows`) · SQLite state · **WiX v4/v5** installer.

## Code signing

Windows release binaries are signed with a certificate issued to the **SignPath Foundation**. Free code signing is provided by [SignPath.io](https://about.signpath.io/), with a certificate by the [SignPath Foundation](https://signpath.org/).

**Signing policy.** Release artifacts are built only by the GitHub Actions pipeline in this repository, from tagged source, and signed by SignPath as part of that pipeline. No maintainer holds the signing key.

**Roles**
- *Committers and reviewers:* the AmneziaGeo maintainers ([@bor-project](https://github.com/bor-project)).
- *Release approvers:* the AmneziaGeo maintainers ([@bor-project](https://github.com/bor-project)).

## Privacy

AmneziaGeo collects no analytics or telemetry and sends no personal data to its authors. The program makes outbound connections only to:

- the **VPN endpoint(s)** contained in the configuration you import;
- the **update server** it is configured to check for new releases;
- the **geo-database sources** you configure, to download `geosite` / `geoip` data.

Everything else is routed strictly according to your own routing rules. No usage data leaves your machine.

## License

AmneziaGeo is licensed under the **GNU General Public License v3.0 or later** — see [LICENSE](LICENSE).

It builds on the AmneziaWG engine (`amneziawg-go` / `amneziawg-windows`), licensed by their respective authors.

## Credits

Built on the [Amnezia VPN](https://github.com/amnezia-vpn) ecosystem — the AmneziaWG protocol and engines. AmneziaGeo adds the geo-aware split-tunneling control plane on top.
